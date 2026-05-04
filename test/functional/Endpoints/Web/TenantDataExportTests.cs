// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB;
using System.Net;
using System.Text.Json;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the data export endpoints.
/// </summary>
public sealed class TenantDataExportTests
{
    // ========== Authorization tests ==========

    [Test]
    public async Task RequestExport_Unauthenticated_Returns401()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("\"success\":true")).IsFalse();
    }

    [Test]
    public async Task RequestExport_ViewerRole_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Test Tenant");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("\"success\":true")).IsFalse();
    }

    [Test]
    public async Task RequestExport_MachineAdminRole_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Test Tenant");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("\"success\":true")).IsFalse();
    }

    [Test]
    public async Task RequestExport_TenantAdminRole_Returns200WithJobId()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Test Tenant");
        await SeedMachine(db, tenantId, "export-host");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"jobId\"");
        await Assert.That(body).Contains("\"status\":\"Pending\"");
    }

    // ========== Status endpoint tests ==========

    [Test]
    public async Task ExportStatus_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenant1Id = await SeedTenantWithSubscription(db, "Tenant 1");
        int tenant2Id = await SeedTenantWithSubscription(db, "Tenant 2");
        await SeedMachine(db, tenant1Id, "t1-host");

        // Create export as tenant 1
        HttpClient client1 = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenant1Id, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenant1Id)
            .Build();

        HttpResponseMessage createResponse = await client1.PostAsync("/api/v1/tenants/export", null);
        string createBody = await createResponse.Content.ReadAsStringAsync();

        // Extract jobId from response using proper JSON deserialization.
        using JsonDocument createDoc = JsonDocument.Parse(createBody);
        int jobId = createDoc.RootElement.GetProperty("jobId").GetInt32();
        string jobIdStr = jobId.ToString();

        // Try to access from tenant 2
        HttpClient client2 = new AuthenticatedClientBuilder(factory)
            .WithUserId(2)
            .WithRole(tenant2Id, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenant2Id)
            .Build();

        HttpResponseMessage statusResponse = await client2.GetAsync($"/api/v1/tenants/export/{jobIdStr}");

        await Assert.That(statusResponse.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ExportStatus_ValidJob_ReturnsCorrectStatus()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Test Tenant");
        await SeedMachine(db, tenantId, "status-host");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage createResponse = await client.PostAsync("/api/v1/tenants/export", null);
        string createBody = await createResponse.Content.ReadAsStringAsync();

        // Extract jobId from response using proper JSON deserialization.
        using JsonDocument createDoc = JsonDocument.Parse(createBody);
        int jobId = createDoc.RootElement.GetProperty("jobId").GetInt32();
        string jobIdStr = jobId.ToString();

        HttpResponseMessage statusResponse = await client.GetAsync($"/api/v1/tenants/export/{jobIdStr}");

        await Assert.That(statusResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string statusBody = await statusResponse.Content.ReadAsStringAsync();
        await Assert.That(statusBody).Contains("\"status\":\"Pending\"");
        await Assert.That(statusBody).Contains("\"jobId\":");
    }

    // ========== Empty state tests ==========

    [Test]
    public async Task RequestExport_TenantWithNoMachines_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Empty Tenant");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("\"success\":true")).IsFalse();
    }

    // ========== Seed helpers ==========

    private static async Task<int> SeedTenantWithSubscription(DatabaseContext db, string name)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = name,
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
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        return tenant.Id;
    }

    private static async Task<long> SeedMachine(DatabaseContext db, int tenantId, string hostname)
    {
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = hostname,
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        return machine.Id;
    }
}
