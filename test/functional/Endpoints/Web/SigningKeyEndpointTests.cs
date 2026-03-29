// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for signing key REST endpoints.
/// Tests the full HTTP pipeline: request → auth → endpoint → service → DB → response.
/// </summary>
public sealed class SigningKeyEndpointTests
{
    private static string GenerateValidPublicKey()
    {
        byte[] key = new byte[32];
        Random.Shared.NextBytes(key);

        return Convert.ToBase64String(key);
    }

    private static async Task<(int tenantId, int userId)> SeedTenantAndUser(
        DatabaseContext db, SubscriptionTier tier = SubscriptionTier.Pro)
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
            MachineLimit = 100,
            RetentionDays = 30,
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

        return (tenant.Id, user.Id);
    }

    // ========== Registration: full round-trip ==========

    [Test]
    public async Task RegisterKey_ValidKey_ReturnsKeyWithFingerprint()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        string publicKey = GenerateValidPublicKey();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/signing-keys", new
        {
            Label = "Work MacBook",
            PublicKey = publicKey
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("Work MacBook");
        await Assert.That(body).Contains("fingerprint");
    }

    // ========== Registration → List round-trip ==========

    [Test]
    public async Task RegisterKey_ThenListKeys_ShowsRegisteredKey()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        await client.PostAsJsonAsync("/api/v1/signing-keys", new
        {
            Label = "Key Alpha",
            PublicKey = GenerateValidPublicKey()
        });

        HttpResponseMessage listResponse = await client.GetAsync("/api/v1/signing-keys");

        await Assert.That(listResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await listResponse.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Key Alpha");
        await Assert.That(body).Contains("\"activeCount\":1");
    }

    // ========== Registration → Revoke → List round-trip ==========

    [Test]
    public async Task RegisterKey_ThenRevoke_ListShowsRevokedKey()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage registerResponse = await client.PostAsJsonAsync("/api/v1/signing-keys", new
        {
            Label = "Revoke Target",
            PublicKey = GenerateValidPublicKey()
        });
        string registerBody = await registerResponse.Content.ReadAsStringAsync();

        // Parse the key ID from the response JSON using proper deserialization
        using JsonDocument doc = JsonDocument.Parse(registerBody);
        int keyId = doc.RootElement.GetProperty("data").GetProperty("id").GetInt32();
        string keyIdStr = keyId.ToString();

        HttpResponseMessage revokeResponse = await client.DeleteAsync($"/api/v1/signing-keys/{keyIdStr}");

        await Assert.That(revokeResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // List should now show activeCount = 0
        HttpResponseMessage listResponse = await client.GetAsync("/api/v1/signing-keys");
        string listBody = await listResponse.Content.ReadAsStringAsync();
        await Assert.That(listBody).Contains("\"activeCount\":0");
    }

    // ========== Max key limit enforcement ==========

    [Test]
    public async Task RegisterKey_AtMaxLimit_Returns409()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        // Register 5 keys (the max).
        for (int i = 0; i < 5; i++)
        {
            HttpResponseMessage regResp = await client.PostAsJsonAsync("/api/v1/signing-keys", new
            {
                Label = $"Key {i}",
                PublicKey = GenerateValidPublicKey()
            });
            await Assert.That(regResp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        // 6th should be rejected.
        HttpResponseMessage overLimitResponse = await client.PostAsJsonAsync("/api/v1/signing-keys", new
        {
            Label = "One Too Many",
            PublicKey = GenerateValidPublicKey()
        });

        await Assert.That(overLimitResponse.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        string overLimitBody = await overLimitResponse.Content.ReadAsStringAsync();
        await Assert.That(overLimitBody).Contains("\"success\":false");
        await Assert.That(overLimitBody).Contains("Maximum active signing keys");
    }

    // ========== Cross-tenant isolation ==========

    [Test]
    public async Task ListKeys_DifferentTenant_DoesNotSeeOtherTenantKeys()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenant1Id, int user1Id) = await SeedTenantAndUser(db);
        (int tenant2Id, int user2Id) = await SeedTenantAndUser(db);

        // Register key in tenant 1.
        HttpClient client1 = new AuthenticatedClientBuilder(factory)
            .WithUserId(user1Id)
            .WithRole(tenant1Id, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenant1Id)
            .Build();

        await client1.PostAsJsonAsync("/api/v1/signing-keys", new
        {
            Label = "Tenant 1 Key",
            PublicKey = GenerateValidPublicKey()
        });

        // List keys as tenant 2 user should see nothing.
        HttpClient client2 = new AuthenticatedClientBuilder(factory)
            .WithUserId(user2Id)
            .WithRole(tenant2Id, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenant2Id)
            .Build();

        HttpResponseMessage listResponse = await client2.GetAsync("/api/v1/signing-keys");
        await Assert.That(listResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await listResponse.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"activeCount\":0");
        await Assert.That(body).DoesNotContain("Tenant 1 Key");
    }

    // ========== Revoke edge cases ==========

    [Test]
    public async Task RevokeKey_NonExistentKey_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/signing-keys/99999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task RevokeKey_AlreadyRevoked_ReturnsErrorResponse()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        // Register and revoke a key.
        HttpResponseMessage registerResponse = await client.PostAsJsonAsync("/api/v1/signing-keys", new
        {
            Label = "Double Revoke",
            PublicKey = GenerateValidPublicKey()
        });
        string registerBody = await registerResponse.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(registerBody);
        int keyId = doc.RootElement.GetProperty("data").GetProperty("id").GetInt32();

        HttpResponseMessage firstRevokeResponse = await client.DeleteAsync($"/api/v1/signing-keys/{keyId}");
        await Assert.That(firstRevokeResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Second revoke of the same key should indicate the key cannot be revoked again.
        HttpResponseMessage secondRevokeResponse = await client.DeleteAsync($"/api/v1/signing-keys/{keyId}");
        string secondBody = await secondRevokeResponse.Content.ReadAsStringAsync();

        // The endpoint returns a non-success response (either error payload or non-200 status).
        bool isError = secondBody.Contains("\"success\":false") ||
                       (int)secondRevokeResponse.StatusCode >= 400;
        await Assert.That(isError).IsEqualTo(true);
    }

    // ========== Invalid public key format ==========

    [Test]
    public async Task RegisterKey_InvalidPublicKey_ReturnsBadRequest()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/signing-keys", new
        {
            Label = "Bad Key",
            PublicKey = Convert.ToBase64String(new byte[16])
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":false");
        await Assert.That(body).Contains("Invalid public key");
    }
}
