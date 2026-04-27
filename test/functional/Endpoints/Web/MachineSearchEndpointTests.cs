// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the machine search endpoint.
/// </summary>
public sealed class MachineSearchEndpointTests
{
    private static async Task<int> SeedTenantWithSubscription(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Search Tenant {Guid.NewGuid():N}",
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

        return tenant.Id;
    }

    private static async Task<long> SeedMachine(
        DatabaseContext db,
        int tenantId,
        string hostname,
        OperatingSystems os = OperatingSystems.Ubuntu,
        MachineTypes machineType = MachineTypes.BareMetalServer)
    {
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = hostname,
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = machineType,
            OperatingSystem = os,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        return machine.Id;
    }

    private static async Task SeedMachineStateSummary(
        DatabaseContext db,
        long machineId,
        int tenantId,
        int? cpuPercent = null,
        int? memoryPercent = null,
        int? pendingUpdates = null,
        int? failedServices = null)
    {
        MachineStateSummary state = new()
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = $"host-{machineId}",
            OperatingSystem = 0,
            MachineType = 0,
            Hostname = $"host-{machineId}",
            CpuUsagePercent = cpuPercent,
            MemoryUsagePercent = memoryPercent,
            PendingUpdates = pendingUpdates,
            FailedServices = failedServices,
            HealthStatus = 0,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(state);
    }

    private static HttpClient BuildAuthenticatedClient(FunctionalTestFactory factory, int tenantId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // ========== Basic response structure ==========

    [Test]
    public async Task Search_NoFilters_ReturnsAllMachines()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "search-machine-1");
        await SeedMachine(db, tenantId, "search-machine-2");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/search");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();

        JsonElement data = root.GetProperty("data");
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(2);
        await Assert.That(data.GetProperty("page").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(25);

        JsonElement items = data.GetProperty("items");
        await Assert.That(items.GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task Search_EmptyTenant_ReturnsEmptyResult()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/search");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    // ========== Text search ==========

    [Test]
    public async Task Search_WithSearchQuery_FiltersResults()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "web-production-01");
        await SeedMachine(db, tenantId, "db-production-01");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/search?search=web");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
    }

    // ========== OS filter ==========

    [Test]
    public async Task Search_WithOsFilter_FiltersResults()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "ubuntu-server", os: OperatingSystems.Ubuntu);
        await SeedMachine(db, tenantId, "windows-server", os: OperatingSystems.Windows);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/search?os=Ubuntu");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
    }

    // ========== CPU range filter ==========

    [Test]
    public async Task Search_WithCpuMinFilter_FiltersResults()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        long lowId = await SeedMachine(db, tenantId, "low-cpu");
        await SeedMachineStateSummary(db, lowId, tenantId, cpuPercent: 20);
        long highId = await SeedMachine(db, tenantId, "high-cpu");
        await SeedMachineStateSummary(db, highId, tenantId, cpuPercent: 85);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/search?cpuMin=80");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
    }

    // ========== Pagination ==========

    [Test]
    public async Task Search_PaginationClampedCorrectly()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "test-machine");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/search?page=-1&pageSize=200");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("page").GetInt32()).IsEqualTo(1);
        // pageSize=200 is clamped to 100 by Math.Clamp in the endpoint
        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(100);
    }

    // ========== Response field structure ==========

    [Test]
    public async Task Search_ResponseItems_ContainExpectedFields()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        long machineId = await SeedMachine(db, tenantId, "field-test-machine");
        await SeedMachineStateSummary(db, machineId, tenantId, cpuPercent: 50, memoryPercent: 60);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/search");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement items = doc.RootElement.GetProperty("data").GetProperty("items");
        await Assert.That(items.GetArrayLength()).IsEqualTo(1);

        JsonElement machine = items[0];
        await Assert.That(machine.TryGetProperty("id", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("name", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("healthStatus", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("cpuUsagePercent", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("memoryUsagePercent", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("isOnline", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("pendingUpdates", out _)).IsTrue();
        await Assert.That(machine.TryGetProperty("failedServices", out _)).IsTrue();
    }

    // ========== Tenant isolation ==========

    [Test]
    public async Task Search_DoesNotReturnOtherTenantMachines()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenant1Id = await SeedTenantWithSubscription(db);
        int tenant2Id = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenant1Id, "tenant1-machine");
        await SeedMachine(db, tenant2Id, "tenant2-machine");

        HttpClient client = BuildAuthenticatedClient(factory, tenant1Id);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/search");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
    }

    // ========== Sorting ==========

    [Test]
    public async Task Search_SortByNameDesc_ReturnsSortedResults()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "alpha-machine");
        await SeedMachine(db, tenantId, "zulu-machine");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/search?sortBy=name&sortDir=desc");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement items = doc.RootElement.GetProperty("data").GetProperty("items");
        await Assert.That(items[0].GetProperty("name").GetString()).IsEqualTo("zulu-machine");
        await Assert.That(items[1].GetProperty("name").GetString()).IsEqualTo("alpha-machine");
    }
}
