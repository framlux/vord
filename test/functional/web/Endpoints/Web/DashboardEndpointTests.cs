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
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for dashboard fleet endpoints.
/// </summary>
public sealed class DashboardEndpointTests
{
    private static async Task<int> SeedTenantWithSubscription(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Dashboard Tenant {Guid.NewGuid():N}",
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
            TenantId = tenantId,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        return machine.Id;
    }

    private static HttpClient BuildAuthenticatedClient(FunctionalTestFactory factory, int tenantId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    [Test]
    public async Task DashboardFleet_WithMachines_ReturnsOverviewWithCorrectCounts()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "fleet-a");
        await SeedMachine(db, tenantId, "fleet-b");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/fleet");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // Verify top-level API response structure
        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();

        JsonElement data = root.GetProperty("data");

        // Verify pagination metadata
        await Assert.That(data.GetProperty("page").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(25);
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(2);
        await Assert.That(data.GetProperty("totalPages").GetInt32()).IsEqualTo(1);

        // Verify machines array contains the seeded machines
        JsonElement machines = data.GetProperty("machines");
        await Assert.That(machines.GetArrayLength()).IsEqualTo(2);

        // Verify summary section exists and reflects the seeded data
        JsonElement summary = data.GetProperty("summary");
        await Assert.That(summary.GetProperty("totalMachines").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task DashboardFleet_EmptyTenant_ReturnsZeroCounts()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/fleet");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();

        JsonElement data = root.GetProperty("data");

        // An empty tenant should have zero machines and zero counts
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("machines").GetArrayLength()).IsEqualTo(0);
        await Assert.That(data.GetProperty("totalPages").GetInt32()).IsEqualTo(0);

        JsonElement summary = data.GetProperty("summary");
        await Assert.That(summary.GetProperty("totalMachines").GetInt32()).IsEqualTo(0);
        await Assert.That(summary.GetProperty("onlineMachines").GetInt32()).IsEqualTo(0);
        await Assert.That(summary.GetProperty("offlineCount").GetInt32()).IsEqualTo(0);
        await Assert.That(summary.GetProperty("warningCount").GetInt32()).IsEqualTo(0);
        await Assert.That(summary.GetProperty("criticalCount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task DashboardFleet_Pagination_ClampedToValidRange()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        // pageSize=200 should be clamped to 100, page=-1 should default to 1
        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/fleet?pageSize=200&page=-1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        // Verify the endpoint clamped pageSize from 200 down to 100
        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(100);

        // Verify the endpoint clamped page from -1 up to 1
        await Assert.That(data.GetProperty("page").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task DashboardFleet_Pagination_PageZeroClampedToOne()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        // page=0 should be clamped to 1
        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/fleet?page=0");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        await Assert.That(data.GetProperty("page").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task DashboardFleet_Pagination_PageSizeZeroClampedToOne()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        // pageSize=0 should be clamped to 1 (Math.Clamp with min=1)
        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/fleet?pageSize=0");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task DashboardFleet_Pagination_NegativePageSizeClampedToOne()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        // pageSize=-1 should be clamped to 1 (Math.Clamp with min=1)
        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/fleet?pageSize=-1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task DashboardFleet_Pagination_LargePageBeyondResults_ReturnsEmptyMachineList()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "only-machine");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        // Request a page far beyond the available results
        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/fleet?page=9999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        // The page number should be what was requested since it is valid (> 0)
        await Assert.That(data.GetProperty("page").GetInt32()).IsEqualTo(9999);

        // No machines should be returned for a page beyond the result set
        await Assert.That(data.GetProperty("machines").GetArrayLength()).IsEqualTo(0);

        // Total count should still reflect all machines regardless of page
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task DashboardFleet_MachineListResponse_ContainsExpectedFields()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "field-check-host");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/fleet");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement machines = doc.RootElement.GetProperty("data").GetProperty("machines");
        await Assert.That(machines.GetArrayLength()).IsEqualTo(1);

        JsonElement machine = machines[0];

        // Verify the machine data fields match the FleetMachineDto contract
        await Assert.That(machine.TryGetProperty("id", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("name", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("healthStatus", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("isOnline", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("cpuUsagePercent", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("memoryUsagePercent", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("maxDiskUsagePercent", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("hasDiskHealthIssue", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("hasHardwareIssue", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("pendingUpdates", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("securityUpdates", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("failedServices", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("totalServices", out _)).IsTrue();

        // Verify the machine name matches what we seeded
        await Assert.That(machine.GetProperty("name").GetString()).IsEqualTo("field-check-host");
    }

    [Test]
    public async Task DashboardFleet_SummarySection_ContainsExpectedFields()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "summary-check");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/fleet");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement summary = doc.RootElement.GetProperty("data").GetProperty("summary");

        // Verify the FleetSummaryDto contract fields are present
        await Assert.That(summary.TryGetProperty("totalMachines", out _)).IsTrue();
        await Assert.That(summary.TryGetProperty("onlineMachines", out _)).IsTrue();
        await Assert.That(summary.TryGetProperty("offlineCount", out _)).IsTrue();
        await Assert.That(summary.TryGetProperty("warningCount", out _)).IsTrue();
        await Assert.That(summary.TryGetProperty("criticalCount", out _)).IsTrue();
        await Assert.That(summary.TryGetProperty("securityUpdates", out _)).IsTrue();
    }

    [Test]
    public async Task DashboardFleet_Pagination_DefaultValues_AreApplied()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        // No pagination query params - should use defaults: page=1, pageSize=25
        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/fleet");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        await Assert.That(data.GetProperty("page").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(25);
    }
}
