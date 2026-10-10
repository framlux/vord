// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Pins every server read surface to one rule: a host past its tenant's machine limit shows as
/// paused, never by the health it last reported before it was paused, and never inside an online,
/// offline or health bucket.
/// </summary>
public sealed class MachinePausedSurfacesTests
{
    private sealed class Fixture
    {
        public required FunctionalTestFactory Factory { get; init; }

        public required HttpClient Client { get; init; }

        public required int TenantId { get; init; }

        // Id order. The Free limit is 3, so Ids[3] and Ids[4] are the paused hosts.
        public required List<long> Ids { get; init; }
    }

    // Free tier allows three machines. In id order the fleet reads healthy, healthy, critical,
    // warning, offline, so the two paused hosts are the ones whose frozen health would otherwise
    // land in the warning and offline buckets.
    private static async Task<Fixture> SeedFreeTenantWithFiveMachines(FunctionalTestFactory factory)
    {
        using DatabaseContext db = factory.CreateDbContext();

        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Paused Surfaces {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        await db.InsertWithInt32IdentityAsync(new TenantSubscription
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        UserAccount user = new()
        {
            ExternalId = $"ext-paused-{Guid.NewGuid():N}",
            Username = $"paused-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        await db.InsertAsync(new UserTenantRole
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        short[] health = [0, 0, 2, 1, 3];
        List<long> ids = [];
        for (int i = 0; i < health.Length; i++)
        {
            long id = await SeedMachine(db, tenant.Id, $"machine-{i}");
            await SeedSummary(db, tenant.Id, id, health[i]);
            ids.Add(id);
        }

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithRole(tenant.Id, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenant.Id)
            .Build();

        return new Fixture { Factory = factory, Client = client, TenantId = tenant.Id, Ids = ids };
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
        await db.InsertAsync(new MachineStateSummary
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
        });
    }

    private static async Task<JsonElement> ReadDataAsync(HttpClient client, string url)
    {
        HttpResponseMessage response = await client.GetAsync(url);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        return doc.RootElement.GetProperty("data").Clone();
    }

    private static List<long> IdsOf(JsonElement items)
    {
        return items.EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).Order().ToList();
    }

    private static bool IsPausedIn(JsonElement items, long machineId)
    {
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.GetProperty("id").GetInt64() == machineId)
            {
                return item.GetProperty("isPaused").GetBoolean();
            }
        }

        throw new InvalidOperationException($"Machine {machineId} was missing from the response");
    }

    [Test]
    public async Task FleetOverview_CountsPausedHostsInTheirOwnBucketAndNoHealthBucket()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement overview = await ReadDataAsync(fixture.Client, "/api/v1/dashboard/fleet");
        JsonElement summary = overview.GetProperty("summary");

        // Total still counts every registered host; the paused two leave the warning and offline
        // buckets they would otherwise have filled from their frozen health.
        await Assert.That(summary.GetProperty("totalMachines").GetInt32()).IsEqualTo(5);
        await Assert.That(summary.GetProperty("pausedCount").GetInt32()).IsEqualTo(2);
        await Assert.That(summary.GetProperty("onlineMachines").GetInt32()).IsEqualTo(3);
        await Assert.That(summary.GetProperty("warningCount").GetInt32()).IsEqualTo(0);
        await Assert.That(summary.GetProperty("offlineCount").GetInt32()).IsEqualTo(0);
        await Assert.That(summary.GetProperty("criticalCount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task FleetOverview_RowsCarryIsPaused()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement machines = (await ReadDataAsync(fixture.Client, "/api/v1/dashboard/fleet")).GetProperty("machines");

        for (int i = 0; i < 5; i++)
        {
            await Assert.That(IsPausedIn(machines, fixture.Ids[i])).IsEqualTo(i >= 3);
        }
    }

    [Test]
    public async Task FleetOverview_HealthFiltersNeverReturnAPausedHost()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement warning = (await ReadDataAsync(fixture.Client, "/api/v1/dashboard/fleet?status=warning")).GetProperty("machines");
        JsonElement offline = (await ReadDataAsync(fixture.Client, "/api/v1/dashboard/fleet?status=offline")).GetProperty("machines");
        JsonElement healthy = (await ReadDataAsync(fixture.Client, "/api/v1/dashboard/fleet?status=healthy")).GetProperty("machines");

        await Assert.That(warning.GetArrayLength()).IsEqualTo(0);
        await Assert.That(offline.GetArrayLength()).IsEqualTo(0);
        await Assert.That(IdsOf(healthy)).IsEquivalentTo(new List<long> { fixture.Ids[0], fixture.Ids[1] });
    }

    [Test]
    public async Task FleetOverview_PausedFilter_ReturnsExactlyThePausedHosts()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement overview = await ReadDataAsync(fixture.Client, "/api/v1/dashboard/fleet?status=paused");

        await Assert.That(IdsOf(overview.GetProperty("machines"))).IsEquivalentTo(new List<long> { fixture.Ids[3], fixture.Ids[4] });
        await Assert.That(overview.GetProperty("totalCount").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task Search_HealthFilterNeverReturnsAPausedHost()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement offline = (await ReadDataAsync(fixture.Client, "/api/v1/machines/search?healthStatus=offline")).GetProperty("items");

        await Assert.That(offline.GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Search_PausedFilter_ReturnsExactlyThePausedHosts()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement paused = await ReadDataAsync(fixture.Client, "/api/v1/machines/search?paused=true");

        await Assert.That(IdsOf(paused.GetProperty("items"))).IsEquivalentTo(new List<long> { fixture.Ids[3], fixture.Ids[4] });
        await Assert.That(paused.GetProperty("totalCount").GetInt32()).IsEqualTo(2);
        await Assert.That(IsPausedIn(paused.GetProperty("items"), fixture.Ids[3])).IsTrue();
    }

    [Test]
    public async Task Search_PausedFalse_ReturnsOnlyActiveHosts()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement active = (await ReadDataAsync(fixture.Client, "/api/v1/machines/search?paused=false")).GetProperty("items");

        await Assert.That(IdsOf(active)).IsEquivalentTo(new List<long> { fixture.Ids[0], fixture.Ids[1], fixture.Ids[2] });
    }

    [Test]
    public async Task MachineIds_HealthFilterExcludesPaused_AndPausedFilterSelectsThem()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement offline = await ReadDataAsync(fixture.Client, "/api/v1/machines/ids?healthStatus=offline");
        JsonElement paused = await ReadDataAsync(fixture.Client, "/api/v1/machines/ids?paused=true");

        await Assert.That(offline.GetProperty("ids").GetArrayLength()).IsEqualTo(0);
        await Assert.That(paused.GetProperty("ids").EnumerateArray().Select(id => id.GetInt64()).Order().ToList())
            .IsEquivalentTo(new List<long> { fixture.Ids[3], fixture.Ids[4] });
    }

    [Test]
    public async Task MachineList_ItemsCarryIsPaused()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement items = (await ReadDataAsync(fixture.Client, "/api/v1/machines")).GetProperty("items");

        for (int i = 0; i < 5; i++)
        {
            await Assert.That(IsPausedIn(items, fixture.Ids[i])).IsEqualTo(i >= 3);
        }
    }

    [Test]
    public async Task MachineList_OnlineFilter_ExcludesPausedHosts()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement online = await ReadDataAsync(fixture.Client, "/api/v1/machines?status=Online");

        await Assert.That(IdsOf(online.GetProperty("items"))).IsEquivalentTo(new List<long> { fixture.Ids[0], fixture.Ids[1], fixture.Ids[2] });
        await Assert.That(online.GetProperty("totalCount").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task MachineList_OfflineFilter_ExcludesPausedHosts()
    {
        // The paused host whose frozen health is Offline must not appear under Offline.
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement offline = await ReadDataAsync(fixture.Client, "/api/v1/machines?status=Offline");

        await Assert.That(offline.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(offline.GetProperty("totalCount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task MachineList_PausedFilter_ReturnsExactlyThePausedHosts()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement paused = await ReadDataAsync(fixture.Client, "/api/v1/machines?status=Paused");

        await Assert.That(IdsOf(paused.GetProperty("items"))).IsEquivalentTo(new List<long> { fixture.Ids[3], fixture.Ids[4] });
        await Assert.That(paused.GetProperty("totalCount").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task MachineStatus_CarriesIsPaused()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement paused = await ReadDataAsync(fixture.Client, $"/api/v1/machines/{fixture.Ids[4]}/status");
        JsonElement active = await ReadDataAsync(fixture.Client, $"/api/v1/machines/{fixture.Ids[0]}/status");

        await Assert.That(paused.GetProperty("isPaused").GetBoolean()).IsTrue();
        await Assert.That(active.GetProperty("isPaused").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task MachineBasicDetail_CarriesIsPaused()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement paused = await ReadDataAsync(fixture.Client, $"/api/v1/machines/{fixture.Ids[3]}");
        JsonElement active = await ReadDataAsync(fixture.Client, $"/api/v1/machines/{fixture.Ids[2]}");

        await Assert.That(paused.GetProperty("isPaused").GetBoolean()).IsTrue();
        await Assert.That(active.GetProperty("isPaused").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task MachineFullDetail_CarriesIsPausedAndTheOneBasedActiveRank()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement lastPaused = await ReadDataAsync(fixture.Client, $"/api/v1/machines/{fixture.Ids[4]}/detail");
        JsonElement active = await ReadDataAsync(fixture.Client, $"/api/v1/machines/{fixture.Ids[1]}/detail");

        await Assert.That(lastPaused.GetProperty("isPaused").GetBoolean()).IsTrue();
        await Assert.That(lastPaused.GetProperty("activeRank").GetInt32()).IsEqualTo(5);
        await Assert.That(active.GetProperty("isPaused").GetBoolean()).IsFalse();
        await Assert.That(active.GetProperty("activeRank").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task DashboardSummary_ReportsPausedHostsAndLeavesThemOutOfOnline()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement summary = await ReadDataAsync(fixture.Client, "/api/v1/dashboard/summary");

        await Assert.That(summary.GetProperty("totalMachines").GetInt32()).IsEqualTo(5);
        await Assert.That(summary.GetProperty("pausedMachines").GetInt32()).IsEqualTo(2);

        // The paused warning host is still reachable by its heartbeat; it must not count as online.
        await Assert.That(summary.GetProperty("onlineMachines").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task DashboardSummary_AgreesWithTheFleetOverviewOnOnlineAndPaused()
    {
        using FunctionalTestFactory factory = new();
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);

        JsonElement summary = await ReadDataAsync(fixture.Client, "/api/v1/dashboard/summary");
        JsonElement fleet = (await ReadDataAsync(fixture.Client, "/api/v1/dashboard/fleet")).GetProperty("summary");

        await Assert.That(summary.GetProperty("onlineMachines").GetInt32()).IsEqualTo(fleet.GetProperty("onlineMachines").GetInt32());
        await Assert.That(summary.GetProperty("pausedMachines").GetInt32()).IsEqualTo(fleet.GetProperty("pausedCount").GetInt32());
    }

    [Test]
    public async Task EverySurface_PauseDecisionUnavailable_ShowsHostsByTheirHealthAndCountsTheFailure()
    {
        // A broken pause check fails open: no page may error or hide a host, and the counter is the
        // only evidence that the limit stopped being shown.
        using FunctionalTestFactory factory = new()
        {
            AdditionalTestServices = services =>
            {
                services.RemoveAll<IQuotaPauseService>();
                services.AddScoped(_ => TestQuotaPause.Unavailable());
            },
        };
        Fixture fixture = await SeedFreeTenantWithFiveMachines(factory);
        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> failures = new(meterFactory, VordMeter.Name, "vord.quota.pause_check_failures");

        JsonElement fleet = await ReadDataAsync(fixture.Client, "/api/v1/dashboard/fleet");
        JsonElement list = await ReadDataAsync(fixture.Client, "/api/v1/machines");
        JsonElement detail = await ReadDataAsync(fixture.Client, $"/api/v1/machines/{fixture.Ids[4]}/detail");

        JsonElement summary = fleet.GetProperty("summary");
        await Assert.That(summary.GetProperty("pausedCount").GetInt32()).IsEqualTo(0);
        await Assert.That(summary.GetProperty("offlineCount").GetInt32()).IsEqualTo(1);
        await Assert.That(summary.GetProperty("warningCount").GetInt32()).IsEqualTo(1);
        await Assert.That(fleet.GetProperty("machines").EnumerateArray().Any(m => m.GetProperty("isPaused").GetBoolean())).IsFalse();
        await Assert.That(list.GetProperty("items").EnumerateArray().Any(m => m.GetProperty("isPaused").GetBoolean())).IsFalse();
        await Assert.That(detail.GetProperty("isPaused").GetBoolean()).IsFalse();

        // The rank does not depend on the pause decision, so it is still reported.
        await Assert.That(detail.GetProperty("activeRank").GetInt32()).IsEqualTo(5);

        // One failed check per request that asked: the fleet overview, the list and the detail.
        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot().Where(m => m.Value > 0).ToList();
        await Assert.That(measurements.Count).IsEqualTo(3);
        await Assert.That(measurements.All(m => (string?)m.Tags["surface"] == "display")).IsTrue();
    }
}
