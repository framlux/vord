// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for registration token REST endpoints (create, revoke, list).
/// Covers error paths including missing fields, non-existent resources, cross-tenant isolation,
/// already-revoked tokens, and empty result sets.
/// </summary>
public sealed class RegistrationTokenEndpointTests
{
    /// <summary>
    /// Seeds a tenant with an active subscription and a MachineAdmin user.
    /// </summary>
    private static async Task<(int TenantId, int UserId)> SeedTenantAndUser(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"RegToken Tenant {Guid.NewGuid():N}",
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
            ExternalId = $"ext-regtoken-{Guid.NewGuid():N}",
            Username = $"regtokenuser-{Guid.NewGuid():N}@example.com",
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

        return (tenant.Id, user.Id);
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

    // ========== POST /api/v1/machines/registration-tokens ==========

    /// <summary>
    /// Submitting a create request with an empty name should fail validation before the handler runs.
    /// The validator requires a non-empty name, so the response must be 400 with an error body.
    /// </summary>
    [Test]
    public async Task CreateToken_MissingName_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/machines/registration-tokens",
            new { Name = "" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Token name is required");
    }

    // ========== DELETE /api/v1/machines/registration-tokens/{id} ==========

    /// <summary>
    /// Attempting to revoke a token whose ID does not exist returns 404
    /// with an error payload describing the missing resource.
    /// </summary>
    [Test]
    public async Task RevokeToken_NonExistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync(
            "/api/v1/machines/registration-tokens/999999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":false");
        await Assert.That(body).Contains("Registration token not found");
    }

    /// <summary>
    /// A token that belongs to a different tenant is opaque to the requesting tenant.
    /// The endpoint must return 404, not 403, to prevent resource enumeration.
    /// </summary>
    [Test]
    public async Task RevokeToken_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenant1Id, int user1Id) = await SeedTenantAndUser(db);
        (int tenant2Id, int user2Id) = await SeedTenantAndUser(db);

        // Create a token belonging to tenant 2.
        HttpClient client2 = BuildClient(factory, tenant2Id, user2Id);
        HttpResponseMessage createResponse = await client2.PostAsJsonAsync(
            "/api/v1/machines/registration-tokens",
            new { Name = "Tenant 2 Token" });

        await Assert.That(createResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string createBody = await createResponse.Content.ReadAsStringAsync();
        using JsonDocument createDoc = JsonDocument.Parse(createBody);
        long tokenId = createDoc.RootElement.GetProperty("data").GetProperty("id").GetInt64();

        // Tenant 1 user attempts to revoke tenant 2's token — must see 404.
        HttpClient client1 = BuildClient(factory, tenant1Id, user1Id);
        HttpResponseMessage revokeResponse = await client1.DeleteAsync(
            $"/api/v1/machines/registration-tokens/{tokenId}");

        await Assert.That(revokeResponse.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        string revokeBody = await revokeResponse.Content.ReadAsStringAsync();
        await Assert.That(revokeBody).Contains("\"success\":false");
        await Assert.That(revokeBody).Contains("Registration token not found");
    }

    /// <summary>
    /// Revoking an already-revoked token returns a non-success response.
    /// The repository returns 0 rows updated for a token that is already revoked,
    /// which the handler treats as a not-found condition (returns 404).
    /// </summary>
    [Test]
    public async Task RevokeToken_AlreadyRevoked_ReturnsErrorResponse()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        // Create a token.
        HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            "/api/v1/machines/registration-tokens",
            new { Name = "Token To Double-Revoke" });

        await Assert.That(createResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string createBody = await createResponse.Content.ReadAsStringAsync();
        using JsonDocument createDoc = JsonDocument.Parse(createBody);
        long tokenId = createDoc.RootElement.GetProperty("data").GetProperty("id").GetInt64();

        // First revoke succeeds.
        HttpResponseMessage firstRevoke = await client.DeleteAsync(
            $"/api/v1/machines/registration-tokens/{tokenId}");
        await Assert.That(firstRevoke.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Second revoke of the already-revoked token must return a non-success status.
        HttpResponseMessage secondRevoke = await client.DeleteAsync(
            $"/api/v1/machines/registration-tokens/{tokenId}");

        string secondBody = await secondRevoke.Content.ReadAsStringAsync();
        bool isErrorResponse = secondBody.Contains("\"success\":false") ||
                               ((int)secondRevoke.StatusCode >= 400);
        await Assert.That(isErrorResponse).IsTrue();
    }

    // ========== GET /api/v1/machines/registration-tokens ==========

    /// <summary>
    /// When a tenant has no registration tokens the list endpoint must return
    /// HTTP 200 with a success payload containing an empty items array and a total count of zero.
    /// </summary>
    [Test]
    public async Task ListTokens_EmptyTenant_ReturnsEmptyList()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/machines/registration-tokens");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        int totalCount = data.GetProperty("totalCount").GetInt32();
        int itemCount = data.GetProperty("items").GetArrayLength();

        await Assert.That(totalCount).IsEqualTo(0);
        await Assert.That(itemCount).IsEqualTo(0);
    }
}
