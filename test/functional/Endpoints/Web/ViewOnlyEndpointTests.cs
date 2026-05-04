// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using System.Net;
using System.Text.Json;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for ViewOnly-policy REST endpoints (machines, telemetry, dashboard, billing).
/// </summary>
public sealed class ViewOnlyEndpointTests
{
    [Test]
    public async Task MachineList_Pagination_ReturnsCorrectPage()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db);
        for (int i = 0; i < 10; i++)
        {
            await SeedMachine(db, tenantId, $"machine-{i:D2}");
        }

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines?page=2&pageSize=3");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":10");

        using JsonDocument doc = JsonDocument.Parse(body);
        int itemCount = doc.RootElement.GetProperty("data").GetProperty("items").GetArrayLength();
        await Assert.That(itemCount).IsEqualTo(3);
    }

    [Test]
    public async Task MachineList_ExcludesDeletedMachines()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "active-host");
        long deletedId = await SeedMachine(db, tenantId, "deleted-host");

        // Soft-delete the second machine
        await db.Machines
            .Where(m => m.Id == deletedId)
            .Set(m => m.IsDeleted, true)
            .Set(m => m.DeletedOn, DateTimeOffset.UtcNow)
            .UpdateAsync();

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":1");
        await Assert.That(body).Contains("active-host");
        await Assert.That(body.Contains("deleted-host")).IsFalse();
    }

    [Test]
    public async Task MachineDetail_ValidMachine_ReturnsData()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db);
        long machineId = await SeedMachine(db, tenantId, "prod-web-01");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("prod-web-01");
        await Assert.That(body).Contains("\"machineType\":");
        await Assert.That(body).Contains("\"operatingSystem\":");
    }

    [Test]
    public async Task DashboardSummary_ReturnsFleetStatistics()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "fleet-1");
        await SeedMachine(db, tenantId, "fleet-2");
        await SeedMachine(db, tenantId, "fleet-3");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/summary");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalMachines\":3");
    }

    [Test]
    public async Task BillingSubscription_ReturnsSubscriptionDetails()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Pro);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("\"tier\":\"Pro\"");
        await Assert.That(body).Contains("\"retentionDays\":30");
    }

    [Test]
    public async Task BillingSubscription_NoSubscription_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Create a tenant without a subscription
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = "No Sub Tenant",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenant.Id, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenant.Id)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("\"success\":true")).IsFalse();
    }

    private static async Task<int> SeedTenantWithSubscription(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro)
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
