// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Integration;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Integration.Repositories;

/// <summary>
/// Verifies against real Postgres that the paused predicate translates the same way in a projection,
/// a filter and a count. It is a comparison folded into the fleet row query, and the SQLite suite
/// cannot show how Postgres renders a boolean built from one.
/// </summary>
public sealed class FleetPausedQueryLiveTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _migratedConnectionString = _fixture.ConnectionString;
        await RunMigrationsAsync(_migratedConnectionString);
    }

    [After(Class)]
    public static async Task AfterClass() => await _fixture.DisposeAsync();

    [Test]
    public async Task Aggregation_AfterCutoff_KeepsPausedMachinesOutOfEveryHealthBucket()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        int tenantId = await SeedTenantAsync(db);
        List<long> ids = await SeedFleetAsync(db, tenantId, 0, 0, 2, 1, 3);

        (List<(short HealthStatus, int Count)> counts, _, int pausedCount) =
            await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.After, ids[2], CancellationToken.None);

        Dictionary<short, int> byStatus = counts.ToDictionary(x => x.HealthStatus, x => x.Count);
        await Assert.That(pausedCount).IsEqualTo(2);
        await Assert.That(byStatus.Keys.Order().ToArray()).IsEquivalentTo(new short[] { 0, 2 });
        await Assert.That(byStatus[0]).IsEqualTo(2);
    }

    [Test]
    public async Task Aggregation_AllPaused_LeavesNoHealthBucket()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        int tenantId = await SeedTenantAsync(db);
        await SeedFleetAsync(db, tenantId, 0, 2, 3);

        (List<(short HealthStatus, int Count)> counts, _, int pausedCount) =
            await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.All, 0, CancellationToken.None);

        await Assert.That(counts.Count).IsEqualTo(0);
        await Assert.That(pausedCount).IsEqualTo(3);
    }

    [Test]
    public async Task Search_FlagsPausedRowsAndHealthFiltersNeverMatchThem()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        int tenantId = await SeedTenantAsync(db);
        List<long> ids = await SeedFleetAsync(db, tenantId, 0, 0, 2, 1, 3);

        (List<FleetMachineRow> rows, int total) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[2], SortBy = "name", Skip = 0, Take = 10 }, CancellationToken.None);
        (List<FleetMachineRow> offline, int offlineTotal) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[2], HealthStatusValues = [3], Skip = 0, Take = 10 }, CancellationToken.None);

        Dictionary<long, bool> pausedById = rows.ToDictionary(r => r.Id, r => r.IsPaused);
        await Assert.That(total).IsEqualTo(5);
        await Assert.That(pausedById[ids[2]]).IsFalse();
        await Assert.That(pausedById[ids[3]]).IsTrue();
        await Assert.That(pausedById[ids[4]]).IsTrue();
        await Assert.That(offline.Count).IsEqualTo(0);
        await Assert.That(offlineTotal).IsEqualTo(0);
    }

    [Test]
    public async Task Search_PausedFilter_CountsAndPagesOnlyPausedMachines()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        int tenantId = await SeedTenantAsync(db);
        List<long> ids = await SeedFleetAsync(db, tenantId, 0, 0, 2, 1, 3);

        (List<FleetMachineRow> page, int total) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[2], Paused = true, SortBy = "name", Skip = 1, Take = 1 }, CancellationToken.None);
        (List<FleetMachineRow> active, int activeTotal) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[2], Paused = false, Skip = 0, Take = 10 }, CancellationToken.None);
        (List<long> pausedIds, int idsTotal) = await repo.SearchFleetMachineIdsAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[2], Paused = true }, 1000, CancellationToken.None);

        await Assert.That(total).IsEqualTo(2);
        await Assert.That(page.Count).IsEqualTo(1);
        await Assert.That(page[0].IsPaused).IsTrue();
        await Assert.That(activeTotal).IsEqualTo(3);
        await Assert.That(active.Any(r => r.IsPaused)).IsFalse();
        await Assert.That(pausedIds).IsEquivalentTo(new List<long> { ids[3], ids[4] });
        await Assert.That(idsTotal).IsEqualTo(2);
    }

    [Test]
    public async Task Search_AllPaused_FlagsEveryRowAndThePausedFilterMatchesAll()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        int tenantId = await SeedTenantAsync(db);
        await SeedFleetAsync(db, tenantId, 0, 2);

        (List<FleetMachineRow> rows, int total) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { AllPaused = true, Paused = true, Skip = 0, Take = 10 }, CancellationToken.None);

        await Assert.That(total).IsEqualTo(2);
        await Assert.That(rows.All(r => r.IsPaused)).IsTrue();
    }

    [Test]
    public async Task Search_SortByStatus_PutsPausedRowsAfterEveryHealthStatus()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        int tenantId = await SeedTenantAsync(db);
        List<long> ids = await SeedFleetAsync(db, tenantId, 2, 3, 0, 1, 0);

        (List<FleetMachineRow> ascending, _) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[1], SortBy = "status", Skip = 0, Take = 10 }, CancellationToken.None);
        (List<FleetMachineRow> descending, _) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[1], SortBy = "status", SortDescending = true, Skip = 0, Take = 10 }, CancellationToken.None);

        await Assert.That(string.Join(",", ascending.Take(2).Select(r => r.Id))).IsEqualTo($"{ids[0]},{ids[1]}");
        await Assert.That(ascending.Skip(2).All(r => r.IsPaused)).IsTrue();
        await Assert.That(descending.Take(3).All(r => r.IsPaused)).IsTrue();
        await Assert.That(string.Join(",", descending.Skip(3).Select(r => r.Id))).IsEqualTo($"{ids[1]},{ids[0]}");
    }

    [Test]
    public async Task Aggregation_AfterCutoff_LeavesPausedMachinesOutOfTheSecurityUpdateTotal()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        int tenantId = await SeedTenantAsync(db);
        List<long> ids = await SeedFleetAsync(db, tenantId, 0, 1, 2);
        int[] updates = [2, 3, 40];
        for (int i = 0; i < ids.Count; i++)
        {
            long machineId = ids[i];
            int count = updates[i];
            await db.MachineStateSummaries.Where(s => s.MachineId == machineId).Set(s => s.SecurityUpdates, count).UpdateAsync();
        }

        (_, int total, _) = await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.After, ids[0], CancellationToken.None);
        (_, int allTotal, _) = await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.All, 0, CancellationToken.None);

        await Assert.That(total).IsEqualTo(2);
        await Assert.That(allTotal).IsEqualTo(0);
    }

    [Test]
    public async Task RegisteredRank_IsTheOneBasedPositionAmongTheTenantsLiveMachines()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        int tenantId = await SeedTenantAsync(db);
        int otherTenantId = await SeedTenantAsync(db);
        List<long> first = await SeedFleetAsync(db, tenantId, 0);
        await SeedFleetAsync(db, otherTenantId, 0);
        List<long> second = await SeedFleetAsync(db, tenantId, 0);

        await Assert.That(await repo.GetRegisteredRankAsync(tenantId, first[0], CancellationToken.None)).IsEqualTo(1);
        await Assert.That(await repo.GetRegisteredRankAsync(tenantId, second[0], CancellationToken.None)).IsEqualTo(2);
    }

    private static async Task RunMigrationsAsync(string connectionString)
    {
        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Warning));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    private static DatabaseContext CreateContext()
    {
        DataOptions<DatabaseContext> options = new(
            new DataOptions().UsePostgreSQL(_migratedConnectionString));

        return new DatabaseContext(options);
    }

    private static async Task<int> SeedTenantAsync(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Live Test Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        };

        return await db.InsertWithInt32IdentityAsync(tenant);
    }

    // Machines are inserted in order, so the returned ids ascend in step with the given statuses.
    private static async Task<List<long>> SeedFleetAsync(DatabaseContext db, int tenantId, params short[] healthStatuses)
    {
        List<long> ids = [];
        foreach (short healthStatus in healthStatuses)
        {
            Machine machine = new()
            {
                TenantId = tenantId,
                ApiKeyHash = Guid.NewGuid().ToString("N"),
                Name = $"Test Machine {Guid.NewGuid():N}",
                SerialNumber = Guid.NewGuid().ToString("N"),
                SystemId = Guid.NewGuid().ToString("N"),
                MachineType = MachineTypes.BareMetalServer,
                OperatingSystem = OperatingSystems.Ubuntu,
                RegistrationTokenId = 0,
                RegisteredOn = DateTimeOffset.UtcNow,
                IsDeleted = false,
            };
            machine.Id = await db.InsertWithInt64IdentityAsync(machine);

            await db.InsertAsync(new MachineStateSummary
            {
                MachineId = machine.Id,
                TenantId = tenantId,
                Name = machine.Name,
                OperatingSystem = (byte)OperatingSystems.Ubuntu,
                MachineType = (byte)MachineTypes.BareMetalServer,
                HealthStatus = healthStatus,
            });

            ids.Add(machine.Id);
        }

        return ids;
    }
}
