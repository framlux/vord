// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
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
            CreatedByUserId = 1,
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
            CreatedByUserId = 1,
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
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
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

    private static async Task<long> SeedAuthorizedMachineAfter(
        DatabaseContext db, int tenantId, int userId, int signingKeyId)
    {
        RegistrationToken regToken = new()
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Second Test Token",
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false,
        };
        regToken.Id = await db.InsertWithInt64IdentityAsync(regToken);

        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = "newer-machine",
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            AssetTagNumber = null,
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = regToken.Id,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        await db.InsertWithInt32IdentityAsync(new MachineAuthorizedKey
        {
            MachineId = machine.Id,
            SigningKeyId = signingKeyId,
            TenantId = tenantId,
            AuthorizedAt = DateTimeOffset.UtcNow,
            AuthorizedByUserId = userId,
        });

        return machine.Id;
    }

    private static async Task EnableCommandsCapability(FunctionalTestFactory factory, long machineId)
    {
        IMachinePingService pingService = factory.Services.GetRequiredService<IMachinePingService>();
        await pingService.SetAgentCapabilitiesAsync(machineId, 1UL);
    }

    /// <summary>
    /// Builds a command request whose canonical payload is signed with the given key. The payload carries
    /// the command type and parameters the request carries, so the server finds them matching unless a
    /// test overrides what was signed. The server compares only those two fields with the request, so the
    /// other signed fields are placeholders.
    /// </summary>
    private static object BuildCommandRequest(
        long machineId,
        int signingKeyId,
        NSec.Cryptography.Key privateKey,
        string? commandId = null,
        string commandType = "reboot",
        string? commandParams = null,
        string? signedCommandType = null,
        string? signedParamsJson = null)
    {
        NSec.Cryptography.SignatureAlgorithm algorithm = NSec.Cryptography.SignatureAlgorithm.Ed25519;
        string id = commandId ?? Guid.NewGuid().ToString("D");
        string nonce = Guid.NewGuid().ToString("N");
        string payload = TestCanonicalPayload.Build(
            signedCommandType ?? commandType,
            signedParamsJson ?? (string.IsNullOrWhiteSpace(commandParams) ? "{}" : commandParams),
            id,
            nonce,
            machineId);
        byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        byte[] signature = algorithm.Sign(privateKey, payloadBytes);

        return new
        {
            CommandId = id,
            MachineId = machineId,
            SigningKeyId = signingKeyId,
            CommandType = commandType,
            Params = commandParams,
            Nonce = nonce,
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

    // ========== Paused host ==========

    [Test]
    public async Task SubmitCommand_HostBeyondTheMachineLimit_Returns409WithTheReasonAndStoresNothing()
    {
        // Intent: with a limit of one machine the oldest host keeps taking commands and the newer one
        // is refused with the reason in the response body, so the UI can show the user why instead of
        // a generic failure. A paused host never polls, so an accepted command would sit unseen.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long keptMachineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        long pausedMachineId = await SeedAuthorizedMachineAfter(db, tenantId, userId, signingKeyId);
        await db.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = tenantId,
            MachineLimit = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await EnableCommandsCapability(factory, keptMachineId);
        await EnableCommandsCapability(factory, pausedMachineId);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage pausedResponse = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(pausedMachineId, signingKeyId, privateKey));
        HttpResponseMessage keptResponse = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(keptMachineId, signingKeyId, privateKey));

        await Assert.That(pausedResponse.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        using JsonDocument pausedBody = JsonDocument.Parse(await pausedResponse.Content.ReadAsStringAsync());
        await Assert.That(pausedBody.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(pausedBody.RootElement.GetProperty("message").GetString())
            .IsEqualTo("Host is paused: your plan's host limit is reached.");
        await Assert.That(keptResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await db.RemoteCommands.CountAsync(c => c.MachineId == pausedMachineId)).IsEqualTo(0);
        await Assert.That(await db.RemoteCommands.CountAsync(c => c.MachineId == keptMachineId)).IsEqualTo(1);
    }

    [Test]
    public async Task SubmitCommand_DuplicateCommandId_ReportsTheServiceReasonInTheBody()
    {
        // Intent: every service refusal reaches the client with its own reason, not one generic
        // message, so the 409 for a paused host is not special-cased in the endpoint.
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
        await client.PostAsJsonAsync("/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandId));

        HttpResponseMessage second = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandId));

        using JsonDocument body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(body.RootElement.GetProperty("message").GetString()).IsEqualTo("Duplicate command ID");
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
        string payload = TestCanonicalPayload.Build(string.Empty);
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

    // ========== Cluster reporter commands ==========

    private const string KubernetesFingerprint = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";

    private static string LocalReportJson(bool controlPlane, bool adminCredentialPresent, bool reporterEnrolled, string? reporterError = null)
    {
        string errorField = (reporterError is null) ? string.Empty : $",\"reporter_error\":\"{reporterError}\"";

        return "{\"distro\":\"k3s\",\"kubelet_version\":\"v1.30.4+k3s1\",\"node_name\":\"cp-1\",\"node_name_source\":\"certificate\","
            + $"\"has_kubelet\":true,\"control_plane\":{(controlPlane ? "true" : "false")},\"ca_fingerprint\":\"{KubernetesFingerprint}\","
            + $"\"admin_credential_present\":{(adminCredentialPresent ? "true" : "false")},\"reporter_enrolled\":{(reporterEnrolled ? "true" : "false")}{errorField}}}";
    }

    private static async Task SeedLocalReportAsync(
        DatabaseContext db, int tenantId, long machineId, string payload, TimeSpan? age = null)
    {
        DateTimeOffset receivedAt = DateTimeOffset.UtcNow - (age ?? TimeSpan.FromMinutes(5));
        await db.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachineTelemetry(
            machineId: machineId, tenantId: tenantId, telemetryType: 14, payload: payload, receivedAt: receivedAt, serverReceivedAt: receivedAt));
    }

    private static async Task SeedCandidateAsync(DatabaseContext db, int tenantId, long machineId, string? reporterError = null)
    {
        long clusterId = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = KubernetesFingerprint,
            Distro = "k3s",
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        });
        await db.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            MachineId = machineId,
            LastReportAt = DateTimeOffset.UtcNow,
            ReporterError = reporterError,
        });
    }

    private static HttpClient BuildMachineAdminClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static async Task<string?> ErrorMessageOfAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("message").GetString();
    }

    [Test]
    [Arguments("k8s_reporter_enrol")]
    [Arguments("k8s_reporter_uninstall")]
    public async Task SubmitCommand_EnrolOrUninstallOnAControlPlaneWithAnAdminCredential_IsStored(string commandType)
    {
        // Intent: a control-plane host whose latest local report shows an admin credential may be sent the
        // commands that read it, through the same signed path as every other command.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: false));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: commandType));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        RemoteCommand stored = await db.RemoteCommands.SingleAsync(c => c.MachineId == machineId);
        await Assert.That(stored.CommandType).IsEqualTo(commandType);
        await Assert.That(stored.Status).IsEqualTo(RemoteCommandStatus.Pending);
    }

    [Test]
    [Arguments("k8s_reporter_enrol")]
    [Arguments("k8s_reporter_uninstall")]
    public async Task SubmitCommand_EnrolOrUninstallOnAHostWithoutAnAdminCredential_Returns400AndStoresNothing(string commandType)
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: false, reporterEnrolled: false));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: commandType));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response)).IsEqualTo("Only control-plane machines with an admin credential can enrol");
        await Assert.That(await db.RemoteCommands.CountAsync(c => c.MachineId == machineId)).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitCommand_EnrolOnAMachineThatNeverReportedKubernetes_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_enrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response)).IsEqualTo("Only control-plane machines with an admin credential can enrol");
    }

    [Test]
    public async Task SubmitCommand_EnrolWhenTheOnlyReportIsOlderThanSevenDays_Returns400()
    {
        // Intent: a stale report is no evidence about the host today, so it does not make it eligible.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(
            db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: false), age: TimeSpan.FromDays(8));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_enrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task SubmitCommand_EnrolDecidesOnTheLatestReportNotAnOlderOne()
    {
        // Intent: eligibility follows the machine's current state. An older report that showed an admin
        // credential does not outvote a newer one that no longer does.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(
            db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: false), age: TimeSpan.FromHours(2));
        await SeedLocalReportAsync(
            db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: false, reporterEnrolled: false), age: TimeSpan.FromMinutes(5));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_enrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task SubmitCommand_UnenrolOnACandidateMachine_IsStored()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedCandidateAsync(db, tenantId, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_unenrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await db.RemoteCommands.CountAsync(c => (c.MachineId == machineId) && (c.CommandType == "k8s_reporter_unenrol"))).IsEqualTo(1);
    }

    [Test]
    public async Task SubmitCommand_UnenrolOnAMachineWhoseReportSaysEnrolled_IsStored()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: true));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_unenrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task SubmitCommand_UnenrolOnAMachineThatIsNotAReporter_Returns400AndStoresNothing()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: false));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_unenrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response)).IsEqualTo("This machine is not an enrolled reporter");
        await Assert.That(await db.RemoteCommands.CountAsync(c => c.MachineId == machineId)).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitCommand_UnenrolIgnoresAnotherTenantsCandidateRowForTheSameMachineId()
    {
        // Intent: a candidate row only counts for the tenant that owns it. A row another tenant holds
        // under the same machine id must not make this tenant's machine look enrolled.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        (int otherTenantId, int _, long _, int _, NSec.Cryptography.Key _) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedCandidateAsync(db, otherTenantId, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_unenrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    [Arguments("K8S_REPORTER_ENROL")]
    [Arguments("k8s_reporter_Unenrol")]
    [Arguments("K8s_Reporter_Uninstall")]
    public async Task SubmitCommand_MixedCaseKubernetesType_Returns400InvalidCommandTypeEvenForAnEligibleMachine(string commandType)
    {
        // Intent: the allowlist ignores case, so the guard must not be skippable by changing it.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: true));
        await SeedCandidateAsync(db, tenantId, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: commandType));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response)).IsEqualTo("Invalid command type");
        await Assert.That(await db.RemoteCommands.CountAsync(c => c.MachineId == machineId)).IsEqualTo(0);
    }

    [Test]
    [Arguments("k8s_reporter_enrol")]
    [Arguments("k8s_reporter_unenrol")]
    [Arguments("k8s_reporter_uninstall")]
    public async Task SubmitCommand_KubernetesCommandWithParameters_Returns400(string commandType)
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: true));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands",
            BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: commandType, commandParams: "{\"node\":\"cp-1\"}"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response)).IsEqualTo("This command takes no parameters");
        await Assert.That(await db.RemoteCommands.CountAsync(c => c.MachineId == machineId)).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitCommand_KubernetesCommandWithAnEmptyParamsObject_IsStored()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: false));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands",
            BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_enrol", commandParams: "{}"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(SubscriptionTier.Free, "k8s_reporter_enrol")]
    [Arguments(SubscriptionTier.Free, "k8s_reporter_unenrol")]
    [Arguments(SubscriptionTier.Free, "k8s_reporter_uninstall")]
    [Arguments(SubscriptionTier.Pro, "k8s_reporter_enrol")]
    [Arguments(SubscriptionTier.Pro, "k8s_reporter_unenrol")]
    [Arguments(SubscriptionTier.Pro, "k8s_reporter_uninstall")]
    public async Task SubmitCommand_ReporterCommandFromATenantBelowTeam_IsStoredForAnEligibleHost(SubscriptionTier tier, string commandType)
    {
        // Intent: cluster reporting is open to every tier. A valid signed command for an eligible host is
        // accepted and stored whatever the tenant pays for, and no Team message is involved.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db, tier);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: true));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: commandType));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        RemoteCommand stored = await db.RemoteCommands.SingleAsync(c => c.MachineId == machineId);
        await Assert.That(stored.CommandType).IsEqualTo(commandType);
        await Assert.That(stored.Status).IsEqualTo(RemoteCommandStatus.Pending);
    }

    [Test]
    [Arguments(SubscriptionTier.Free)]
    [Arguments(SubscriptionTier.Pro)]
    public async Task GetCommandHistoryAndDetail_TenantBelowTeam_ShowsTheReporterCommandItSent(SubscriptionTier tier)
    {
        // Intent: a tenant below Team can send the reporter commands, and what the agent then did is only
        // visible in the machine's command history. The list and the detail must therefore answer for every
        // tier, or those tenants would have no way to see a refusal or a failure.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db, tier);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: true));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);
        HttpResponseMessage submitResponse = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_unenrol"));
        await Assert.That(submitResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        RemoteCommand stored = await db.RemoteCommands.SingleAsync(c => c.MachineId == machineId);

        HttpResponseMessage historyResponse = await client.GetAsync($"/api/v1/machines/{machineId}/commands");
        HttpResponseMessage detailResponse = await client.GetAsync($"/api/v1/commands/{stored.Id}");

        await Assert.That(historyResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await historyResponse.Content.ReadAsStringAsync()).Contains("k8s_reporter_unenrol");
        await Assert.That(detailResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await detailResponse.Content.ReadAsStringAsync()).Contains("k8s_reporter_unenrol");
    }

    [Test]
    [Arguments(SubscriptionTier.Free)]
    [Arguments(SubscriptionTier.Pro)]
    public async Task SubmitCommand_ReporterCommandFromATenantBelowTeam_StillFacesEligibility(SubscriptionTier tier)
    {
        // Intent: opening the commands to every tier moves only the Team check. The service still refuses a
        // host that cannot take the command, with its own reason.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db, tier);
        await EnableCommandsCapability(factory, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_enrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response)).IsEqualTo("Only control-plane machines with an admin credential can enrol");
    }

    [Test]
    [Arguments(SubscriptionTier.Free)]
    [Arguments(SubscriptionTier.Pro)]
    public async Task SubmitCommand_RequestNamesAReporterCommandButTheSignatureCoversAnotherType_IsRefusedAtSendTime(SubscriptionTier tier)
    {
        // Intent: the Team check judges the type the request names, and the agent runs the type that was
        // signed. A tenant below Team that names a reporter command to get past the check while signing
        // something else is refused, so the check can never be skipped by naming the wrong type.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db, tier);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: true));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands",
            BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_enrol", signedCommandType: "reboot"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response)).IsEqualTo("Canonical payload does not match the request");
        await Assert.That(await db.RemoteCommands.CountAsync(c => c.MachineId == machineId)).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitCommand_RequestNamesRebootOnAProTenantButTheSignatureCoversAReporterCommand_IsRefusedByTheTeamCheck()
    {
        // Intent: the type the request names decides the Team check, so naming a command that needs Team
        // is refused for a Pro tenant whatever was signed.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db, SubscriptionTier.Pro);
        await EnableCommandsCapability(factory, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands",
            BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "reboot", signedCommandType: "k8s_reporter_enrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ErrorMessageOfAsync(response)).IsEqualTo("Remote commands require a Team subscription");
    }

    [Test]
    public async Task SubmitCommand_PayloadSignsDifferentParamsThanTheRequest_Returns400AtSendTime()
    {
        // Intent: a params mismatch used to fail on the host, when the agent compared the delivered command
        // with the signed one. The server now catches it when the command is sent.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands",
            BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "kill_process", commandParams: """{"pid":"42"}""", signedParamsJson: """{"pid":"43"}"""));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response)).IsEqualTo("Canonical payload does not match the request");
        await Assert.That(await db.RemoteCommands.CountAsync(c => c.MachineId == machineId)).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitCommand_NullParamsWithASignedEmptyObject_IsStored()
    {
        // Intent: this is exactly what the web sends, so the match must hold for it.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands",
            BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "reboot", commandParams: null, signedParamsJson: "{}"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        RemoteCommand stored = await db.RemoteCommands.SingleAsync(c => c.MachineId == machineId);
        await Assert.That(stored.Params).IsNull();
    }

    [Test]
    [Arguments("""{"pid":42}""")]
    [Arguments("""{"flag":true}""")]
    [Arguments("""{"pid":null}""")]
    [Arguments("""{"nested":{"a":"b"}}""")]
    [Arguments("""{"list":["a"]}""")]
    [Arguments("[]")]
    [Arguments("\"text\"")]
    [Arguments("not json")]
    [Arguments("""{"pid":"1","pid":"2"}""")]
    [Arguments("""{"\ud800":"x"}""")]
    [Arguments("""{"a":"\ud800"}""")]
    [Arguments("""{"session_id":"\u0000"}""")]
    [Arguments("""{"\u0000":"x"}""")]
    public async Task SubmitCommand_ParamsThatAreNotAnObjectOfStrings_Returns400WithTheParamsMessage(string commandParams)
    {
        // Intent: the agent refuses signed params that are not strings, so the server refuses the request
        // with a validation error on Params instead of queueing a command that cannot run.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands",
            BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "kill_process", commandParams: commandParams, signedParamsJson: "{}"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Params must be a JSON object of string values");
        await Assert.That(await db.RemoteCommands.CountAsync(c => c.MachineId == machineId)).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitCommand_ParamsThatAreAnObjectOfStrings_AreStoredAsSent()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands",
            BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "kill_process", commandParams: """{"pid":"42"}"""));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        RemoteCommand stored = await db.RemoteCommands.SingleAsync(c => c.MachineId == machineId);
        await Assert.That(stored.Params).IsEqualTo("""{"pid":"42"}""");
    }

    [Test]
    [Arguments("k8s_reporter_unenrol")]
    [Arguments("k8s_reporter_uninstall")]
    public async Task SubmitCommand_CleanupCommandOnAnEnrolledHostWithoutTheCommandsCapability_IsStored(string commandType)
    {
        // Intent: remote commands switched off on a host must not strand the reporter's objects in the
        // cluster. A host that enrolled can still be sent the commands that remove them.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: true));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: commandType));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await db.RemoteCommands.CountAsync(c => (c.MachineId == machineId) && (c.CommandType == commandType))).IsEqualTo(1);
    }

    [Test]
    public async Task SubmitCommand_UninstallOnAHostThatOnlyHasACandidateRowAndNoCommandsCapability_IsStored()
    {
        // Intent: a candidate row is evidence of enrolment, so it earns the cleanup bypass even when the
        // host's latest report does not say it is enrolled.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: false));
        await SeedCandidateAsync(db, tenantId, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_uninstall"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task SubmitCommand_UninstallOnAControlPlaneThatNeverEnrolledAndHasNoCommandsCapability_Returns400WithTheRemoteCommandsReason()
    {
        // Intent: skipping the capability check is for cleaning up what the reporter installed. A control
        // plane that never enrolled has nothing to clean, so it is not sent a command it switched off.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: false));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_uninstall"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response))
            .IsEqualTo("This host has remote commands disabled and never enrolled; enable remote commands to uninstall from it");
        await Assert.That(await db.RemoteCommands.CountAsync(c => c.MachineId == machineId)).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitCommand_UninstallOnACopiedRecordWithoutTheCommandsCapability_Returns400AndStoresNothing()
    {
        // Intent: a disk clone of an enrolled control plane reports itself enrolled with state_copied, but
        // its agent holds no record of its own and refuses an uninstall while remote commands are off.
        // The server refuses it at send time rather than answering "Accepted" for a command that is then
        // rejected on the host.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: true, reporterError: "state_copied"));
        await SeedCandidateAsync(db, tenantId, machineId, "state_copied");
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_uninstall"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response))
            .IsEqualTo("This host has remote commands disabled and its reporter state was copied from another host; unenrol it, or enable remote commands to uninstall from it");
        await Assert.That(await db.RemoteCommands.CountAsync(c => c.MachineId == machineId)).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitCommand_UnenrolOnACopiedRecordWithoutTheCommandsCapability_IsStored()
    {
        // Intent: unenrol is the remedy for a copied record and removes only local files, so the agent
        // accepts it with remote commands off and the server sends it.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: true, reporterError: "state_copied"));
        await SeedCandidateAsync(db, tenantId, machineId, "state_copied");
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_unenrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await db.RemoteCommands.CountAsync(c => (c.MachineId == machineId) && (c.CommandType == "k8s_reporter_unenrol"))).IsEqualTo(1);
    }

    [Test]
    public async Task SubmitCommand_UninstallOnACopiedRecordWithTheCommandsCapability_IsStored()
    {
        // Intent: with remote commands on the capability check applies as it always did, and uninstall
        // works by label across the whole cluster whatever record the host holds.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: true, reporterError: "state_copied"));
        await SeedCandidateAsync(db, tenantId, machineId, "state_copied");
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_uninstall"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task SubmitCommand_UninstallOnAControlPlaneThatNeverEnrolledWithTheCommandsCapability_IsStored()
    {
        // Intent: with remote commands on nothing changed. Uninstall can still clean up an orphaned
        // install whose server-side state was already collected.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: false));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_uninstall"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task SubmitCommand_UnenrolOnAHostThatNeverEnrolledAndHasNoCommandsCapability_Returns400NotAnEnrolledReporter()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: false));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_unenrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response)).IsEqualTo("This machine is not an enrolled reporter");
    }

    [Test]
    public async Task SubmitCommand_CleanupCommandOnAPausedHostWithoutTheCommandsCapability_Returns409()
    {
        // Intent: the agent does not poll while paused, so even a cleanup command is refused with the
        // reason, and the capability bypass does not extend to it.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long keptMachineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        long pausedMachineId = await SeedAuthorizedMachineAfter(db, tenantId, userId, signingKeyId);
        await db.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = tenantId,
            MachineLimit = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await SeedLocalReportAsync(db, tenantId, pausedMachineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: true));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(pausedMachineId, signingKeyId, privateKey, commandType: "k8s_reporter_uninstall"));

        await Assert.That(keptMachineId).IsNotEqualTo(pausedMachineId);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await db.RemoteCommands.CountAsync(c => c.MachineId == pausedMachineId)).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitCommand_IneligibleHostThatIsAlsoPaused_Returns400NotTheConflict()
    {
        // Intent: a refusal that waiting cannot fix is reported ahead of a pause that may lift.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long keptMachineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        long pausedMachineId = await SeedAuthorizedMachineAfter(db, tenantId, userId, signingKeyId);
        await db.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = tenantId,
            MachineLimit = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await EnableCommandsCapability(factory, keptMachineId);
        await EnableCommandsCapability(factory, pausedMachineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage ineligible = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(pausedMachineId, signingKeyId, privateKey, commandType: "k8s_reporter_enrol"));
        HttpResponseMessage reboot = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(pausedMachineId, signingKeyId, privateKey));

        await Assert.That(ineligible.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(ineligible)).IsEqualTo("Only control-plane machines with an admin credential can enrol");
        await Assert.That(reboot.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task SubmitCommand_IneligibleHostWithoutTheCommandsCapability_Returns400WithTheEligibilityReason()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_enrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response)).IsEqualTo("Only control-plane machines with an admin credential can enrol");
    }

    [Test]
    public async Task SubmitCommand_EligibleHostWithoutTheCommandsCapability_Returns400ForTheCapability()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await SeedLocalReportAsync(db, tenantId, machineId, LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: false));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "k8s_reporter_enrol"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorMessageOfAsync(response)).IsEqualTo("Remote commands are not enabled on this machine");
    }

    [Test]
    public async Task SubmitCommand_EnrolDecidesOnWhenTheServerAcceptedTheReportNotTheAgentsTimestamp()
    {
        // Intent: a report from an agent whose clock runs slow carries an old ReceivedAt, but the server
        // accepted it a moment ago, so it still makes the machine eligible. A report the server accepted
        // eight days ago does not, however fresh the agent says it is.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long slowClockMachineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        long fastClockMachineId = await SeedAuthorizedMachineAfter(db, tenantId, userId, signingKeyId);
        await EnableCommandsCapability(factory, slowClockMachineId);
        await EnableCommandsCapability(factory, fastClockMachineId);
        string eligibleReport = LocalReportJson(controlPlane: true, adminCredentialPresent: true, reporterEnrolled: false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await db.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachineTelemetry(
            machineId: slowClockMachineId, tenantId: tenantId, telemetryType: 14, payload: eligibleReport,
            receivedAt: now.AddDays(-7).AddHours(-1), serverReceivedAt: now.AddMinutes(-1)));
        await db.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachineTelemetry(
            machineId: fastClockMachineId, tenantId: tenantId, telemetryType: 14, payload: eligibleReport,
            receivedAt: now, serverReceivedAt: now.AddDays(-8)));
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage slowClock = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(slowClockMachineId, signingKeyId, privateKey, commandType: "k8s_reporter_enrol"));
        HttpResponseMessage fastClock = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(fastClockMachineId, signingKeyId, privateKey, commandType: "k8s_reporter_enrol"));

        await Assert.That(slowClock.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(fastClock.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task SubmitCommand_BlankParams_AreStoredAsNull(string blankParams)
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandParams: blankParams));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        RemoteCommand stored = await db.RemoteCommands.SingleAsync(c => c.MachineId == machineId);
        await Assert.That(stored.Params).IsNull();
    }

    [Test]
    public async Task SubmitCommand_ExistingCommandsAreUnaffectedByTheKubernetesCheck()
    {
        // Intent: a machine that never reported Kubernetes can still be sent every pre-existing command.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedFullEnvironment(db);
        await EnableCommandsCapability(factory, machineId);
        HttpClient client = BuildMachineAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/commands", BuildCommandRequest(machineId, signingKeyId, privateKey, commandType: "check_updates"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
