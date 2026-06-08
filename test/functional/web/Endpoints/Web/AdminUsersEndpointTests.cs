// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the global admin users listing endpoint (<c>GET /api/v1/admin/users</c>).
/// The endpoint is protected by the <c>"Admin"</c> policy which requires the <c>iga=True</c> claim.
/// </summary>
public sealed class AdminUsersEndpointTests
{
    private sealed record SeededFixture(int TenantA, int TenantB, int AdminUserId, int OtherUserId);

    private static async Task<SeededFixture> SeedTwoTenantsAndTwoUsers(DatabaseContext db)
    {
        Tenant tenantA = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Admin Users Tenant A {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenantA.Id = await db.InsertWithInt32IdentityAsync(tenantA);

        Tenant tenantB = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Admin Users Tenant B {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenantB.Id = await db.InsertWithInt32IdentityAsync(tenantB);

        UserAccount adminUser = new()
        {
            ExternalId = $"ext-admin-users-admin-{Guid.NewGuid():N}",
            Username = $"adminusers-admin-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = true,
        };
        adminUser.Id = await db.InsertWithInt32IdentityAsync(adminUser);

        UserAccount otherUser = new()
        {
            ExternalId = $"ext-admin-users-other-{Guid.NewGuid():N}",
            Username = $"adminusers-other-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        otherUser.Id = await db.InsertWithInt32IdentityAsync(otherUser);

        // Place the admin in tenant A and the other user in tenant B so we can assert the endpoint
        // returns users from BOTH tenants (it is global, not tenant-scoped).
        await db.InsertAsync(new UserTenantRole
        {
            UserId = adminUser.Id,
            AssignedTenantId = tenantA.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = adminUser.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        await db.InsertAsync(new UserTenantRole
        {
            UserId = otherUser.Id,
            AssignedTenantId = tenantB.Id,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = otherUser.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        return new SeededFixture(tenantA.Id, tenantB.Id, adminUser.Id, otherUser.Id);
    }

    [Test]
    public async Task GetUsers_AsGlobalAdmin_ReturnsAllUsersAcrossAllTenants()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededFixture seeded = await SeedTwoTenantsAndTwoUsers(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(seeded.AdminUserId)
            .WithRole(seeded.TenantA, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(seeded.TenantA)
            .AsGlobalAdmin()
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/admin/users");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();

        JsonElement users = root.GetProperty("data");
        await Assert.That(users.ValueKind).IsEqualTo(JsonValueKind.Array);

        // The endpoint returns every user — including the seeded admin and other-tenant viewer.
        List<int> ids = users.EnumerateArray().Select(u => u.GetProperty("id").GetInt32()).ToList();
        await Assert.That(ids).Contains(seeded.AdminUserId);
        await Assert.That(ids).Contains(seeded.OtherUserId);

        // Each user DTO carries its tenant memberships — verifying we don't accidentally
        // collapse to a tenant-scoped list.
        JsonElement adminDto = users.EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == seeded.AdminUserId);
        await Assert.That(adminDto.GetProperty("isGlobalAdmin").GetBoolean()).IsTrue();
        await Assert.That(adminDto.GetProperty("username").GetString()).IsNotEmpty();

        JsonElement otherDto = users.EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == seeded.OtherUserId);
        await Assert.That(otherDto.GetProperty("isGlobalAdmin").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task GetUsers_AsNonAdmin_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededFixture seeded = await SeedTwoTenantsAndTwoUsers(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(seeded.OtherUserId)
            .WithRole(seeded.TenantB, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(seeded.TenantB)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/admin/users");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task GetUsers_AsTenantAdminWithoutGlobalAdminFlag_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededFixture seeded = await SeedTwoTenantsAndTwoUsers(db);

        // TenantAdmin role alone is not enough — the "Admin" policy requires the iga claim.
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(seeded.OtherUserId)
            .WithRole(seeded.TenantB, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(seeded.TenantB)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/admin/users");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task GetUsers_Unauthenticated_Returns401()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/admin/users");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}
