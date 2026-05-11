// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for remote command REST endpoints.
/// Tests the full HTTP pipeline including signed command submission and retrieval.
/// </summary>
public sealed class CommandEndpointTests
{
    private static async Task<(int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey)> SeedFullEnvironment(
        DatabaseContext db, SubscriptionTier tier = SubscriptionTier.Team)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Test Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-{Guid.NewGuid():N}",
            Username = $"user-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        UserTenantRole role = new()
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.MachineAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };
        await db.InsertAsync(role);

        // Seed a registration token first (needed for Machine FK).
        RegistrationToken regToken = new()
        {
            TenantId = tenant.Id,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Test Token",
            CreatedByUserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false,
        };
        regToken.Id = await db.InsertWithInt64IdentityAsync(regToken);

        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = "test-machine",
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            AssetTagNumber = null,
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = regToken.Id,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenant.Id
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        // Generate a real Ed25519 keypair for signing commands.
        NSec.Cryptography.SignatureAlgorithm algorithm = NSec.Cryptography.SignatureAlgorithm.Ed25519;
        NSec.Cryptography.Key privateKey = NSec.Cryptography.Key.Create(algorithm);
        byte[] keyBytes = privateKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);

        UserSigningKey signingKey = new()
        {
            UserId = user.Id,
            TenantId = tenant.Id,
            Label = "Test Signing Key",
            PublicKey = Convert.ToBase64String(keyBytes),
            PublicKeyFingerprint = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(keyBytes)),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        signingKey.Id = await db.InsertWithInt32IdentityAsync(signingKey);

        // Authorize the signing key for the machine so remote commands are permitted.
        MachineAuthorizedKey authorization = new()
        {
            MachineId = machine.Id,
            SigningKeyId = signingKey.Id,
            TenantId = tenant.Id,
            AuthorizedAt = DateTimeOffset.UtcNow,
            AuthorizedByUserId = user.Id,
        };
        await db.InsertWithInt32IdentityAsync(authorization);

        return (tenant.Id, user.Id, machine.Id, signingKey.Id, privateKey);
    }

    private static async Task EnableCommandsCapability(FunctionalTestFactory factory, long machineId)
    {
        IMachinePingService pingService = factory.Services.GetRequiredService<IMachinePingService>();
        await pingService.SetAgentCapabilitiesAsync(machineId, 1UL);
    }

    private static object BuildCommandRequest(long machineId, int signingKeyId, NSec.Cryptography.Key privateKey, string? commandId = null)
    {
        NSec.Cryptography.SignatureAlgorithm algorithm = NSec.Cryptography.SignatureAlgorithm.Ed25519;
        string payload = "{}";
        byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        byte[] signature = algorithm.Sign(privateKey, payloadBytes);

        return new
        {
            CommandId = commandId ?? Guid.NewGuid().ToString("D"),
            MachineId = machineId,
            SigningKeyId = signingKeyId,
            CommandType = "reboot",
            Nonce = Guid.NewGuid().ToString("N"),
            Signature = Convert.ToBase64String(signature),
            CanonicalPayload = payload,
            Timestamp = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
    }

    // ========== Submit → Retrieve round-trip ==========

    [Test]
    public async Task SubmitCommand_ThenGetHistory_ShowsCommand()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        // Submit command.
        HttpResponseMessage submitResponse = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey));

        await Assert.That(submitResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string submitBody = await submitResponse.Content.ReadAsStringAsync();
        await Assert.That(submitBody).Contains("\"success\":true");
        await Assert.That(submitBody).Contains("reboot");

        // Retrieve command history.
        HttpResponseMessage historyResponse = await client.GetAsync($"/api/v1/machines/{machineId}/commands");

        await Assert.That(historyResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string historyBody = await historyResponse.Content.ReadAsStringAsync();
        await Assert.That(historyBody).Contains("reboot");
    }

    // ========== Submit → Get detail ==========

    [Test]
    public async Task SubmitCommand_ThenGetDetail_ReturnsCorrectCommand()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage submitResponse = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey));
        string submitBody = await submitResponse.Content.ReadAsStringAsync();

        // Extract command ID from response using proper JSON deserialization.
        using JsonDocument submitDoc = JsonDocument.Parse(submitBody);
        long cmdId = submitDoc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
        string cmdIdStr = cmdId.ToString();

        HttpResponseMessage detailResponse = await client.GetAsync($"/api/v1/commands/{cmdIdStr}");

        await Assert.That(detailResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string detailBody = await detailResponse.Content.ReadAsStringAsync();
        await Assert.That(detailBody).Contains("reboot");
        await Assert.That(detailBody).Contains("Pending");
    }

    // ========== Duplicate command ID prevention (T3) ==========

    [Test]
    public async Task SubmitCommand_DuplicateCommandId_Returns409()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        string commandId = Guid.NewGuid().ToString("D");

        // First submission succeeds.
        HttpResponseMessage first = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandId));
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Second submission with same command ID should fail with 409.
        HttpResponseMessage second = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandId));
        string secondBody = await second.Content.ReadAsStringAsync();
        await Assert.That(secondBody).Contains("\"success\":false");
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    // ========== Viewer role can read but not submit ==========

    [Test]
    public async Task SubmitCommand_ViewerRole_ReturnsForbidden()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);

        HttpClient viewerClient = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await viewerClient.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("\"success\":true")).IsFalse();
    }

    [Test]
    public async Task GetCommandHistory_ViewerRole_Succeeds()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key _) = await SeedFullEnvironment(db);

        HttpClient viewerClient = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await viewerClient.GetAsync($"/api/v1/machines/{machineId}/commands");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    // ========== Empty command history ==========

    [Test]
    public async Task GetCommandHistory_NoCommands_ReturnsEmptyList()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, _, NSec.Cryptography.Key _) = await SeedFullEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/commands");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("[]");
    }

    // ========== T5: Pagination tests ==========

    [Test]
    public async Task GetCommandHistory_Pagination_ReturnsCorrectPages()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        // Submit 3 commands.
        for (int i = 0; i < 3; i++)
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync(
                "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        // Request page 1 with pageSize=2.
        HttpResponseMessage page1Response = await client.GetAsync($"/api/v1/machines/{machineId}/commands?page=1&pageSize=2");
        await Assert.That(page1Response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string page1Body = await page1Response.Content.ReadAsStringAsync();

        // Verify page 1 has 2 results using proper JSON deserialization.
        using JsonDocument page1Doc = JsonDocument.Parse(page1Body);
        int page1Count = page1Doc.RootElement.GetProperty("data").GetArrayLength();
        await Assert.That(page1Count).IsEqualTo(2);

        // Request page 2 with pageSize=2.
        HttpResponseMessage page2Response = await client.GetAsync($"/api/v1/machines/{machineId}/commands?page=2&pageSize=2");
        await Assert.That(page2Response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string page2Body = await page2Response.Content.ReadAsStringAsync();

        using JsonDocument page2Doc = JsonDocument.Parse(page2Body);
        int page2Count = page2Doc.RootElement.GetProperty("data").GetArrayLength();
        await Assert.That(page2Count).IsEqualTo(1);
    }

    // ========== T6: Cross-tenant command submission ==========

    [Test]
    public async Task SubmitCommand_CrossTenantMachine_Rejected()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Seed two separate environments.
        (int tenantA, int userA, long _, int signingKeyA, NSec.Cryptography.Key privateKeyA) = await SeedFullEnvironment(db);
        (int tenantB, int _, long machineBId, int _, NSec.Cryptography.Key _) = await SeedFullEnvironment(db);

        // User A tries to send command to Tenant B's machine.
        HttpClient clientA = new AuthenticatedClientBuilder(factory)
            .WithUserId(userA)
            .WithRole(tenantA, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantA)
            .Build();

        HttpResponseMessage response = await clientA.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineBId, signingKeyA, privateKeyA));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // ========== Tier-gating: remote commands require Team subscription ==========

    [Test]
    public async Task SubmitCommand_ProTierSubscription_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db, SubscriptionTier.Pro);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Team subscription");
    }

    [Test]
    public async Task SubmitCommand_FreeTierSubscription_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db, SubscriptionTier.Free);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Team subscription");
    }

    // ========== Error path: command detail not found ==========

    [Test]
    public async Task CommandDetail_NonExistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _, int _, NSec.Cryptography.Key _) = await SeedFullEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        // Use an ID that cannot exist in the test database.
        HttpResponseMessage response = await client.GetAsync("/api/v1/commands/999999999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Command not found");
    }

    // ========== Error path: command detail for a different tenant ==========

    [Test]
    public async Task CommandDetail_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Seed two separate tenant environments.
        (int tenantA, int userA, long machineA, int signingKeyA, NSec.Cryptography.Key privateKeyA) = await SeedFullEnvironment(db);
        (int tenantB, int userB, long _, int _, NSec.Cryptography.Key _) = await SeedFullEnvironment(db);

        await EnableCommandsCapability(factory, machineA);

        // User A submits a command under tenant A.
        HttpClient clientA = new AuthenticatedClientBuilder(factory)
            .WithUserId(userA)
            .WithRole(tenantA, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantA)
            .Build();

        HttpResponseMessage submitResponse = await clientA.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineA, signingKeyA, privateKeyA));
        await Assert.That(submitResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string submitBody = await submitResponse.Content.ReadAsStringAsync();
        using JsonDocument submitDoc = JsonDocument.Parse(submitBody);
        long cmdId = submitDoc.RootElement.GetProperty("data").GetProperty("id").GetInt64();

        // User B (tenant B) attempts to retrieve the command owned by tenant A.
        HttpClient clientB = new AuthenticatedClientBuilder(factory)
            .WithUserId(userB)
            .WithRole(tenantB, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantB)
            .Build();

        HttpResponseMessage detailResponse = await clientB.GetAsync($"/api/v1/commands/{cmdId}");

        await Assert.That(detailResponse.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        string body = await detailResponse.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Command not found");
    }

    // ========== Error path: send command to a soft-deleted machine ==========

    [Test]
    public async Task CommandSend_DeletedMachine_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);

        // Soft-delete the machine directly in the database.
        await db.Machines
            .Where(m => m.Id == machineId)
            .Set(m => m.IsDeleted, true)
            .UpdateAsync();

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":false");
    }

    // ========== Error path: send command to a machine in a different tenant ==========

    [Test]
    public async Task CommandSend_WrongTenant_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Seed two separate tenant environments.
        (int tenantA, int userA, long _, int signingKeyA, NSec.Cryptography.Key privateKeyA) = await SeedFullEnvironment(db);
        (int tenantB, int _, long machineBId, int _, NSec.Cryptography.Key _) = await SeedFullEnvironment(db);

        await EnableCommandsCapability(factory, machineBId);

        // User A attempts to send a command targeting tenant B's machine.
        HttpClient clientA = new AuthenticatedClientBuilder(factory)
            .WithUserId(userA)
            .WithRole(tenantA, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantA)
            .Build();

        HttpResponseMessage response = await clientA.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineBId, signingKeyA, privateKeyA));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":false");
    }

    // ========== Error path: send command with missing command type ==========

    [Test]
    public async Task CommandSend_MissingCommandType_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        NSec.Cryptography.SignatureAlgorithm algorithm = NSec.Cryptography.SignatureAlgorithm.Ed25519;
        string payload = "{}";
        byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        byte[] signature = algorithm.Sign(privateKey, payloadBytes);

        // Build request with empty CommandType to trigger validator rejection.
        object requestWithEmptyCommandType = new
        {
            CommandId = Guid.NewGuid().ToString("D"),
            MachineId = machineId,
            SigningKeyId = signingKeyId,
            CommandType = "",
            Nonce = Guid.NewGuid().ToString("N"),
            Signature = Convert.ToBase64String(signature),
            CanonicalPayload = payload,
            Timestamp = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/commands", requestWithEmptyCommandType);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Command type is required");
    }
}
