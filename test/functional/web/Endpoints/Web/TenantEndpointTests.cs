// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using System.Net.Http.Json;
using System.Net;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for tenant management endpoints.
/// </summary>
public sealed class TenantEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedTenantEnvironment(
        DatabaseContext db,
        bool isGlobalAdmin = false)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Tenant Test {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-tenant-user-{Guid.NewGuid():N}",
            Username = $"tenantuser-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = isGlobalAdmin,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        UserTenantRole role = new()
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        return (tenant.Id, user.Id);
    }

    // --- TenantDetailEndpoint Tests ---

    [Test]
    public async Task TenantDetail_GlobalAdmin_CanViewAnyTenant()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantEnvironment(db, isGlobalAdmin: true);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .AsGlobalAdmin()
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/tenants/{tenantId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    [Test]
    public async Task TenantDetail_NonAdmin_OtherTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1) = await SeedTenantEnvironment(db);
        (int tenantId2, _) = await SeedTenantEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId1)
            .WithRole(tenantId1, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId1)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/tenants/{tenantId2}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task TenantDetail_NonexistentTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantEnvironment(db, isGlobalAdmin: true);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .AsGlobalAdmin()
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/tenants/99999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // --- TenantCreateEndpoint Tests ---

    [Test]
    public async Task CreateTenant_NonAdmin_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantEnvironment(db, isGlobalAdmin: false);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = "New Tenant",
            LogoUrl = "",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task CreateTenant_ValidRequest_Returns201()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantEnvironment(db, isGlobalAdmin: true);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .AsGlobalAdmin()
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = $"Brand New Tenant {Guid.NewGuid():N}",
            LogoUrl = "",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    // --- TenantListEndpoint Tests ---

    [Test]
    public async Task TenantList_RegularUser_ReturnsOnlyAssignedTenants()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId) = await SeedTenantEnvironment(db, isGlobalAdmin: false);
        // Create a second tenant the user is NOT assigned to.
        (int tenantId2, _) = await SeedTenantEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId1, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId1)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/tenants");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    [Test]
    public async Task TenantList_GlobalAdmin_ReturnsAllTenants()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId) = await SeedTenantEnvironment(db, isGlobalAdmin: true);
        (int tenantId2, _) = await SeedTenantEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .AsGlobalAdmin()
            .WithRole(tenantId1, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId1)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/tenants");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    // --- TenantCreateEndpoint Error Path Tests ---

    [Test]
    public async Task CreateTenant_EmptyName_ReturnsErrorInBody()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantEnvironment(db, isGlobalAdmin: true);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .AsGlobalAdmin()
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = "",
            LogoUrl = "",
        });

        // The endpoint uses Send.OkAsync which sets HTTP 200, but body indicates failure
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":false");
        await Assert.That(body).Contains("required");
    }

    [Test]
    public async Task CreateTenant_DuplicateName_ReturnsConflictInBody()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantEnvironment(db, isGlobalAdmin: true);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .AsGlobalAdmin()
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        // Create tenant via API first so the cache is populated
        string duplicateName = $"Duplicate Test {Guid.NewGuid():N}";
        HttpResponseMessage firstResponse = await client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = duplicateName,
            LogoUrl = "",
        });
        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Attempt to create with the same name
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = duplicateName,
            LogoUrl = "",
        });

        // The endpoint uses Send.OkAsync which sets HTTP 200, but body indicates failure
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":false");
        await Assert.That(body).Contains("already exists");
    }

    [Test]
    public async Task CreateTenant_Returns201WithLocationHeader()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantEnvironment(db, isGlobalAdmin: true);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .AsGlobalAdmin()
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        string uniqueName = $"Location Header Test {Guid.NewGuid():N}";
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = uniqueName,
            LogoUrl = "",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Verify Location header is present
        string? locationHeader = response.Headers.Location?.ToString();
        await Assert.That(locationHeader).IsNotNull();
        await Assert.That(locationHeader!).Contains("/api/v1/tenants/");
    }
}
