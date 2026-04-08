// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB;

namespace Framlux.FleetManagement.FunctionalTest.Authorization;

/// <summary>
/// Systematic role-based access matrix tests for critical endpoints.
/// Verifies that role enforcement is correctly applied across the API.
/// </summary>
public sealed class RoleAccessMatrixTests
{
    private static async Task<(int TenantId, int AdminUserId, int ViewerUserId, long MachineId)> SeedEnvironment(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Matrix Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            RetentionDays = 30,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        // Admin user
        UserAccount admin = new()
        {
            ExternalId = $"ext-admin-{Guid.NewGuid():N}",
            Username = $"admin-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
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

        // Viewer user
        UserAccount viewer = new()
        {
            ExternalId = $"ext-viewer-{Guid.NewGuid():N}",
            Username = $"viewer-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
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

        // Machine
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N").PadLeft(64, '0'),
            Name = "matrix-machine",
            SerialNumber = "sn-matrix-001",
            SystemId = "sid-matrix-001",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenant.Id,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        return (tenant.Id, admin.Id, viewer.Id, machine.Id);
    }

    private static HttpClient BuildViewerClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static HttpClient BuildAdminClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // ========== Machine Delete ==========

    [Test]
    public async Task MachineDelete_Viewer_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int _, int viewerUserId, long machineId) = await SeedEnvironment(db);

        HttpClient client = BuildViewerClient(factory, tenantId, viewerUserId);

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/machines/{machineId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task MachineDelete_Unauthenticated_Returns401Or403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/machines/1");

        bool isRejected = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                          (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isRejected).IsTrue();
    }

    // ========== Cancel Subscription ==========

    [Test]
    public async Task CancelSubscription_Viewer_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int _, int viewerUserId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildViewerClient(factory, tenantId, viewerUserId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task CancelSubscription_TenantAdmin_DoesNotReturn403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int adminUserId, int _, long _) = await SeedEnvironment(db);

        HttpClient client = BuildAdminClient(factory, tenantId, adminUserId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        // Should get through auth (might be 200 or other non-403)
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.Forbidden);
    }

    // ========== Admin Settings ==========

    [Test]
    public async Task AdminSettings_NonAdmin_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int adminUserId, int _, long _) = await SeedEnvironment(db);

        // TenantAdmin but not GlobalAdmin
        HttpClient client = BuildAdminClient(factory, tenantId, adminUserId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/admin/settings");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task AdminUsers_NonAdmin_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int _, int viewerUserId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildViewerClient(factory, tenantId, viewerUserId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/admin/users");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // ========== Invitation Create ==========

    [Test]
    public async Task InvitationCreate_Viewer_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int _, int viewerUserId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildViewerClient(factory, tenantId, viewerUserId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/invitations", new { email = "test@example.com", role = 1 });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // ========== Registration Token Create ==========

    [Test]
    public async Task CreateRegistrationToken_Viewer_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int _, int viewerUserId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildViewerClient(factory, tenantId, viewerUserId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/machines/registration-tokens", new { name = "test" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // ========== Webhook Create ==========

    [Test]
    public async Task WebhookCreate_Viewer_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int _, int viewerUserId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildViewerClient(factory, tenantId, viewerUserId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new { name = "test", url = "https://example.com/hook" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // ========== Alert Rule Create ==========

    [Test]
    public async Task AlertRuleCreate_Viewer_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int _, int viewerUserId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildViewerClient(factory, tenantId, viewerUserId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            name = "test",
            metric = "CpuUsage",
            @operator = "GreaterThan",
            threshold = 80,
            severity = "Warning",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // ========== Data Export ==========

    [Test]
    public async Task DataExport_Viewer_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int _, int viewerUserId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildViewerClient(factory, tenantId, viewerUserId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // ========== Member Management ==========

    [Test]
    public async Task MemberRemove_Viewer_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int _, int viewerUserId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildViewerClient(factory, tenantId, viewerUserId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/members/1/remove", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task MemberRoleChange_Viewer_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int _, int viewerUserId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildViewerClient(factory, tenantId, viewerUserId);

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/members/1/role", new { role = 3 });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // ========== Viewer can access read-only endpoints ==========

    [Test]
    public async Task Viewer_CanAccessMachineList()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int _, int viewerUserId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildViewerClient(factory, tenantId, viewerUserId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines?page=1&pageSize=25");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Viewer_CanAccessBillingInvoices()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int _, int viewerUserId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildViewerClient(factory, tenantId, viewerUserId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/invoices");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
