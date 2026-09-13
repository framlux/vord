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
/// Pins every surface that reports machine liveness to one answer. The dashboard fleet overview,
/// the machine search list, the machine list and the status endpoint used to answer from two
/// different clocks, so a machine could read Offline on one screen and Online on the next.
/// </summary>
public sealed class MachineLivenessAgreementTests
{
    private static async Task<(int TenantId, int UserId)> SeedTenant(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Liveness Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
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
            ExternalId = $"ext-liveness-{Guid.NewGuid():N}",
            Username = $"liveness-{Guid.NewGuid():N}@example.com",
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
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        return (tenant.Id, user.Id);
    }

    private static async Task<long> SeedMachine(DatabaseContext db, int tenantId, string name)
    {
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N").PadLeft(64, '0'),
            Name = name,
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

    private static async Task SeedSummary(DatabaseContext db, int tenantId, long machineId, short healthStatus)
    {
        MachineStateSummary summary = new()
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = $"machine-{machineId}",
            OperatingSystem = (byte)OperatingSystems.Ubuntu,
            MachineType = (byte)MachineTypes.BareMetalServer,
            Hostname = $"host-{machineId}",
            CpuUsagePercent = 20,
            MemoryUsagePercent = 30,
            HealthStatus = healthStatus,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        await db.InsertAsync(summary);
    }

    private static HttpClient BuildClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static async Task<JsonElement> ReadDataAsync(HttpClient client, string url)
    {
        HttpResponseMessage response = await client.GetAsync(url);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        return doc.RootElement.GetProperty("data").Clone();
    }

    private static bool FindOnlineInList(JsonElement items, long machineId)
    {
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.GetProperty("id").GetInt64() == machineId)
            {
                return item.GetProperty("isOnline").GetBoolean();
            }
        }

        throw new InvalidOperationException($"Machine {machineId} was missing from the list response");
    }

    private static async Task<(bool Overview, bool Search, bool List, bool Status)> ReadEverySurfaceAsync(
        HttpClient client,
        long machineId)
    {
        JsonElement overview = await ReadDataAsync(client, "/api/v1/dashboard/fleet");
        JsonElement search = await ReadDataAsync(client, "/api/v1/machines/search");
        JsonElement list = await ReadDataAsync(client, "/api/v1/machines");
        JsonElement status = await ReadDataAsync(client, $"/api/v1/machines/{machineId}/status");

        return (
            FindOnlineInList(overview.GetProperty("machines"), machineId),
            FindOnlineInList(search.GetProperty("items"), machineId),
            FindOnlineInList(list.GetProperty("items"), machineId),
            status.GetProperty("isOnline").GetBoolean());
    }

    [Test]
    public async Task EverySurface_AgreesAMachineSweptOffline_IsOffline()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db);
        long machineId = await SeedMachine(db, tenantId, "offline-machine");
        await SeedSummary(db, tenantId, machineId, healthStatus: 3);

        HttpClient client = BuildClient(factory, tenantId, userId);

        (bool overview, bool search, bool list, bool status) = await ReadEverySurfaceAsync(client, machineId);

        await Assert.That(overview).IsFalse();
        await Assert.That(search).IsEqualTo(overview);
        await Assert.That(list).IsEqualTo(overview);
        await Assert.That(status).IsEqualTo(overview);
    }

    [Test]
    public async Task EverySurface_AgreesAHealthyMachine_IsOnline()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db);
        long machineId = await SeedMachine(db, tenantId, "healthy-machine");
        await SeedSummary(db, tenantId, machineId, healthStatus: 0);

        HttpClient client = BuildClient(factory, tenantId, userId);

        (bool overview, bool search, bool list, bool status) = await ReadEverySurfaceAsync(client, machineId);

        await Assert.That(overview).IsTrue();
        await Assert.That(search).IsEqualTo(overview);
        await Assert.That(list).IsEqualTo(overview);
        await Assert.That(status).IsEqualTo(overview);
    }

    [Test]
    public async Task EverySurface_AgreesACriticalMachine_IsStillOnline()
    {
        // Critical is a verdict about a machine we can still reach. If any surface folded that
        // into offline, the health badge and the liveness LED would contradict each other again.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db);
        long machineId = await SeedMachine(db, tenantId, "critical-machine");
        await SeedSummary(db, tenantId, machineId, healthStatus: 2);

        HttpClient client = BuildClient(factory, tenantId, userId);

        (bool overview, bool search, bool list, bool status) = await ReadEverySurfaceAsync(client, machineId);

        await Assert.That(overview).IsTrue();
        await Assert.That(search).IsEqualTo(overview);
        await Assert.That(list).IsEqualTo(overview);
        await Assert.That(status).IsEqualTo(overview);
    }

    [Test]
    public async Task EverySurface_AgreesAMachineWithNoSummaryRow_IsOffline()
    {
        // A registered machine that has never been heard from has no summary row. Every surface
        // reads that absence the same way rather than each choosing its own default.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db);
        long machineId = await SeedMachine(db, tenantId, "never-seen-machine");

        HttpClient client = BuildClient(factory, tenantId, userId);

        (bool overview, bool search, bool list, bool status) = await ReadEverySurfaceAsync(client, machineId);

        await Assert.That(overview).IsFalse();
        await Assert.That(search).IsEqualTo(overview);
        await Assert.That(list).IsEqualTo(overview);
        await Assert.That(status).IsEqualTo(overview);
    }

    [Test]
    public async Task DashboardSummary_CountsOnlineTheSameWayTheFleetListDoes()
    {
        // The dashboard's online count used to come from Redis while the list beside it came from
        // the swept column, so the headline number could disagree with the rows under it.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db);

        long healthy = await SeedMachine(db, tenantId, "summary-healthy");
        long critical = await SeedMachine(db, tenantId, "summary-critical");
        long offline = await SeedMachine(db, tenantId, "summary-offline");
        await SeedMachine(db, tenantId, "summary-never-seen");

        await SeedSummary(db, tenantId, healthy, healthStatus: 0);
        await SeedSummary(db, tenantId, critical, healthStatus: 2);
        await SeedSummary(db, tenantId, offline, healthStatus: 3);

        HttpClient client = BuildClient(factory, tenantId, userId);

        JsonElement summary = await ReadDataAsync(client, "/api/v1/dashboard/summary");
        JsonElement overview = await ReadDataAsync(client, "/api/v1/dashboard/fleet");

        int onlineFromOverview = overview.GetProperty("machines").EnumerateArray()
            .Count(m => m.GetProperty("isOnline").GetBoolean());

        await Assert.That(summary.GetProperty("totalMachines").GetInt32()).IsEqualTo(4);
        await Assert.That(summary.GetProperty("onlineMachines").GetInt32()).IsEqualTo(2);
        await Assert.That(summary.GetProperty("onlineMachines").GetInt32()).IsEqualTo(onlineFromOverview);
    }
}
