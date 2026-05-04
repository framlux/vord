// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the machine authorized key REST endpoints (add, list, revoke).
/// Exercises the full HTTP pipeline including authentication, authorization, tier gating, and persistence.
/// </summary>
public sealed class MachineAuthorizedKeyEndpointTests
{
    /// <summary>
    /// Seeds a complete environment: tenant, subscription, user, role, machine, and signing key.
    /// Returns all IDs needed for endpoint testing.
    /// </summary>
    private static async Task<(int TenantId, int UserId, long MachineId, int SigningKeyId)> SeedEnvironment(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Team,
        UserAccountRoles role = UserAccountRoles.MachineAdmin)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"AuthKey Tenant {Guid.NewGuid():N}",
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
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-authkey-{Guid.NewGuid():N}",
            Username = $"authkeyuser-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        UserTenantRole userRole = new()
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = role,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(userRole);

        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = $"authkey-machine-{Guid.NewGuid():N}",
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenant.Id,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        // Create a signing key with deterministic 32-byte public key.
        byte[] keyBytes = new byte[32];
        Random.Shared.NextBytes(keyBytes);
        string publicKeyBase64 = Convert.ToBase64String(keyBytes);
        string fingerprint = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(keyBytes));

        UserSigningKey signingKey = new()
        {
            UserId = user.Id,
            TenantId = tenant.Id,
            Label = $"Functional Test Key {Guid.NewGuid():N}",
            PublicKey = publicKeyBase64,
            PublicKeyFingerprint = fingerprint,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        signingKey.Id = await db.InsertWithInt32IdentityAsync(signingKey);

        return (tenant.Id, user.Id, machine.Id, signingKey.Id);
    }

    private static HttpClient BuildClient(
        FunctionalTestFactory factory,
        int tenantId,
        int userId,
        UserAccountRoles clientRole = UserAccountRoles.MachineAdmin)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)clientRole)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // ========== POST /api/v1/machines/{machineId}/authorized-keys ==========

    [Test]
    public async Task AddAuthorizedKey_TeamTier_MachineAdmin_ReturnsOkWithDto()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("signingKeyId");
    }

    [Test]
    public async Task AddAuthorizedKey_TeamTier_TenantAdmin_ReturnsOk()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedEnvironment(
            db, role: UserAccountRoles.TenantAdmin);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.TenantAdmin);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    [Test]
    public async Task AddAuthorizedKey_ViewerRole_Returns403Forbidden()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedEnvironment(
            db, role: UserAccountRoles.Viewer);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task AddAuthorizedKey_FreeTier_Returns403WithSubscriptionMessage()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedEnvironment(
            db, tier: SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Team subscription");
    }

    [Test]
    public async Task AddAuthorizedKey_ProTier_Returns403WithSubscriptionMessage()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedEnvironment(
            db, tier: SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Team subscription");
    }

    [Test]
    public async Task AddAuthorizedKey_NonExistentMachine_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _, int signingKeyId) = await SeedEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/machines/99999/authorized-keys",
            new { SigningKeyId = signingKeyId });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task AddAuthorizedKey_NonExistentSigningKey_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int _) = await SeedEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = 99999 });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task AddAuthorizedKey_DuplicateAuthorization_Returns409Conflict()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        // First authorization should succeed.
        HttpResponseMessage firstResponse = await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });
        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Second authorization for the same key-machine pair should return conflict.
        HttpResponseMessage secondResponse = await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });

        await Assert.That(secondResponse.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        string body = await secondResponse.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("already authorized");
    }

    [Test]
    public async Task AddAuthorizedKey_VerifiesPersistedInDatabase()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });

        MachineAuthorizedKey? stored = await db.MachineAuthorizedKeys
            .FirstOrDefaultAsync(a => (a.MachineId == machineId) && (a.SigningKeyId == signingKeyId));

        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.TenantId).IsEqualTo(tenantId);
        await Assert.That(stored.AuthorizedByUserId).IsEqualTo(userId);
        await Assert.That(stored.RevokedAt).IsNull();
    }

    // ========== GET /api/v1/machines/{machineId}/authorized-keys ==========

    [Test]
    public async Task ListAuthorizedKeys_WithAuthorizations_ReturnsOkWithCorrectCount()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        // Authorize a key first.
        await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });

        // List should return the authorized key.
        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines/{machineId}/authorized-keys");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");

        using JsonDocument doc = JsonDocument.Parse(body);
        int count = doc.RootElement.GetProperty("data").GetArrayLength();
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task ListAuthorizedKeys_ViewerCanRead_ReturnsOk()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedEnvironment(
            db, role: UserAccountRoles.Viewer);

        // Seed an authorization directly in the database since the Viewer cannot POST.
        MachineAuthorizedKey authRecord = new()
        {
            MachineId = machineId,
            SigningKeyId = signingKeyId,
            TenantId = tenantId,
            AuthorizedAt = DateTimeOffset.UtcNow,
            AuthorizedByUserId = userId,
        };
        await db.InsertWithInt32IdentityAsync(authRecord);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines/{machineId}/authorized-keys");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    [Test]
    public async Task ListAuthorizedKeys_NonExistentMachine_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _, int _) = await SeedEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/machines/99999/authorized-keys");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ListAuthorizedKeys_EmptyMachine_ReturnsEmptyArray()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int _) = await SeedEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines/{machineId}/authorized-keys");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"data\":[]");
    }

    // ========== DELETE /api/v1/machines/{machineId}/authorized-keys/{keyId} ==========

    [Test]
    public async Task RevokeAuthorizedKey_ActiveAuthorization_ReturnsOk()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        // Authorize first.
        await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });

        // Revoke the authorization. The DELETE endpoint takes the signing key ID.
        HttpResponseMessage response = await client.DeleteAsync(
            $"/api/v1/machines/{machineId}/authorized-keys/{signingKeyId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("revoked");
    }

    [Test]
    public async Task RevokeAuthorizedKey_NonExistentAuthorization_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int _) = await SeedEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync(
            $"/api/v1/machines/{machineId}/authorized-keys/99999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task RevokeAuthorizedKey_ViewerRole_Returns403Forbidden()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedEnvironment(
            db, role: UserAccountRoles.Viewer);

        // Seed an authorization directly.
        MachineAuthorizedKey authRecord = new()
        {
            MachineId = machineId,
            SigningKeyId = signingKeyId,
            TenantId = tenantId,
            AuthorizedAt = DateTimeOffset.UtcNow,
            AuthorizedByUserId = userId,
        };
        await db.InsertWithInt32IdentityAsync(authRecord);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.DeleteAsync(
            $"/api/v1/machines/{machineId}/authorized-keys/{signingKeyId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task RevokeAuthorizedKey_VerifiesRevokedInDatabase()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        // Authorize, then revoke.
        await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });

        await client.DeleteAsync(
            $"/api/v1/machines/{machineId}/authorized-keys/{signingKeyId}");

        // Verify the record is revoked in the database.
        MachineAuthorizedKey? record = await db.MachineAuthorizedKeys
            .FirstOrDefaultAsync(a => (a.MachineId == machineId) && (a.SigningKeyId == signingKeyId));

        await Assert.That(record).IsNotNull();
        await Assert.That(record!.RevokedAt).IsNotNull();
    }

    [Test]
    public async Task RevokeAuthorizedKey_CrossTenantIsolation_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1, long machineId1, int signingKeyId1) = await SeedEnvironment(db);
        (int tenantId2, int userId2, long _, int _) = await SeedEnvironment(db);

        // Authorize a key in tenant 1.
        HttpClient client1 = BuildClient(factory, tenantId1, userId1);
        await client1.PostAsJsonAsync(
            $"/api/v1/machines/{machineId1}/authorized-keys",
            new { SigningKeyId = signingKeyId1 });

        // User from tenant 2 tries to revoke the authorization from tenant 1.
        HttpClient client2 = BuildClient(factory, tenantId2, userId2);
        HttpResponseMessage response = await client2.DeleteAsync(
            $"/api/v1/machines/{machineId1}/authorized-keys/{signingKeyId1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // ========== End-to-end command submission with authorization ==========

    // NOTE: End-to-end testing of command submission respecting per-machine key authorization
    // is not practical in functional tests because it requires generating valid Ed25519 signatures,
    // constructing canonical payloads, and matching nonce/timestamp constraints. The command
    // authorization check (IsKeyAuthorizedForMachineAsync) is already tested in
    // RemoteCommandServiceTests via NSubstitute mocking and in the authorized key service
    // unit tests above via real database operations.
}
