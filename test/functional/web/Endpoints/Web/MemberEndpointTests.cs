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
/// Functional tests for member management endpoints.
/// </summary>
public sealed class MemberEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedMemberEnvironment(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Member Tenant {Guid.NewGuid():N}",
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
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-member-user-{Guid.NewGuid():N}",
            Username = $"memberuser-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
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

    private static HttpClient BuildClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // --- MemberListEndpoint Tests ---

    [Test]
    public async Task ListMembers_NoTenant_Returns403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/members");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task ListMembers_WithMembers_ReturnsMemberDtos()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedMemberEnvironment(db);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/members");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    // --- MemberRemoveEndpoint Tests ---

    [Test]
    public async Task RemoveMember_NonexistentUser_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedMemberEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/members/99999/remove", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task RemoveMember_SelfRemoval_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedMemberEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/members/{userId}/remove", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RemoveMember_ValidTarget_DisablesRole()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int adminUserId) = await SeedMemberEnvironment(db);

        UserAccount targetUser = new()
        {
            ExternalId = $"ext-target-{Guid.NewGuid():N}",
            Username = $"target-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        targetUser.Id = await db.InsertWithInt32IdentityAsync(targetUser);

        UserTenantRole targetRole = new()
        {
            UserId = targetUser.Id,
            AssignedTenantId = tenantId,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = adminUserId,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(targetRole);

        HttpClient client = BuildClient(factory, tenantId, adminUserId);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/members/{targetUser.Id}/remove", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        UserTenantRole? updatedRole = await db.UserTenantRoles
            .FirstOrDefaultAsync(r => r.UserId == targetUser.Id && r.AssignedTenantId == tenantId);
        await Assert.That(updatedRole!.IsActive).IsFalse();
    }

    [Test]
    public async Task RemoveMember_ViewerInAllSsoTenant_Succeeds()
    {
        // Regression: a Team tenant whose admin signs in via tenant SSO (CustomOidc) has no
        // non-CustomOidc admin, so the last-admin guard would previously misfire on any removal.
        // Removing a non-admin (Viewer) must succeed and not consult the admin guard.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"SSO Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        await db.InsertWithInt32IdentityAsync(new TenantSubscription
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Team,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        UserAccount admin = new()
        {
            ExternalId = $"ext-sso-admin-{Guid.NewGuid():N}",
            Username = $"sso-admin-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
            AuthProvider = AuthProviderType.CustomOidc,
        };
        admin.Id = await db.InsertWithInt32IdentityAsync(admin);
        await db.InsertAsync(new UserTenantRole
        {
            UserId = admin.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = admin.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        UserAccount viewer = new()
        {
            ExternalId = $"ext-sso-viewer-{Guid.NewGuid():N}",
            Username = $"sso-viewer-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
            AuthProvider = AuthProviderType.CustomOidc,
        };
        viewer.Id = await db.InsertWithInt32IdentityAsync(viewer);
        await db.InsertAsync(new UserTenantRole
        {
            UserId = viewer.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = admin.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        HttpClient client = BuildClient(factory, tenant.Id, admin.Id);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/members/{viewer.Id}/remove", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        UserTenantRole? removed = await db.UserTenantRoles
            .FirstOrDefaultAsync(r => r.UserId == viewer.Id && r.AssignedTenantId == tenant.Id);
        await Assert.That(removed!.IsActive).IsFalse();
    }

    // --- MemberRoleChangeEndpoint Tests ---

    [Test]
    public async Task ChangeRole_NonTeamTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedMemberEnvironment(db, SubscriptionTier.Pro);

        UserAccount targetUser = new()
        {
            ExternalId = $"ext-rolechange-{Guid.NewGuid():N}",
            Username = $"rolechange-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        targetUser.Id = await db.InsertWithInt32IdentityAsync(targetUser);

        UserTenantRole targetRole = new()
        {
            UserId = targetUser.Id,
            AssignedTenantId = tenantId,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = userId,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(targetRole);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/members/{targetUser.Id}/role", new
        {
            UserId = targetUser.Id,
            Role = "TenantAdmin",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task ChangeRole_InvalidRole_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedMemberEnvironment(db, SubscriptionTier.Team);

        UserAccount targetUser = new()
        {
            ExternalId = $"ext-badrole-{Guid.NewGuid():N}",
            Username = $"badrole-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        targetUser.Id = await db.InsertWithInt32IdentityAsync(targetUser);

        UserTenantRole targetRole = new()
        {
            UserId = targetUser.Id,
            AssignedTenantId = tenantId,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = userId,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(targetRole);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/members/{targetUser.Id}/role", new
        {
            UserId = targetUser.Id,
            Role = "SuperAdmin",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ChangeRole_SelfChange_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedMemberEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/members/{userId}/role", new
        {
            UserId = userId,
            Role = "Viewer",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ChangeRole_ValidRequest_UpdatesRole()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int adminUserId) = await SeedMemberEnvironment(db, SubscriptionTier.Team);

        UserAccount targetUser = new()
        {
            ExternalId = $"ext-validrole-{Guid.NewGuid():N}",
            Username = $"validrole-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        targetUser.Id = await db.InsertWithInt32IdentityAsync(targetUser);

        UserTenantRole targetRole = new()
        {
            UserId = targetUser.Id,
            AssignedTenantId = tenantId,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = adminUserId,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(targetRole);

        HttpClient client = BuildClient(factory, tenantId, adminUserId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/members/{targetUser.Id}/role", new
        {
            UserId = targetUser.Id,
            Role = "MachineAdmin",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Old role should be disabled
        UserTenantRole? oldRole = await db.UserTenantRoles
            .FirstOrDefaultAsync(r => r.UserId == targetUser.Id && r.AssignedTenantId == tenantId && r.Role == UserAccountRoles.Viewer);
        await Assert.That(oldRole!.IsActive).IsFalse();

        // New role should be active
        UserTenantRole? newRole = await db.UserTenantRoles
            .FirstOrDefaultAsync(r => r.UserId == targetUser.Id && r.AssignedTenantId == tenantId && r.Role == UserAccountRoles.MachineAdmin && r.IsActive);
        await Assert.That(newRole).IsNotNull();
    }
}
