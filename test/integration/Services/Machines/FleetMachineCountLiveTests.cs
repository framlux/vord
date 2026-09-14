// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// Live test for the fleet-wide machine count, against real Postgres.
/// </summary>
/// <remarks>
/// Three properties are load-bearing and none is visible from a substituted unit test: the count
/// spans every tenant, a soft-deleted machine is not a fleet member, and a machine that registered
/// but never reported telemetry counts as Offline rather than vanishing. That last case is the
/// population the ingest-stalled rule most needs to see — an implementation that grouped directly
/// on the state summary table would silently drop it and close the gate on exactly the fleet the
/// rule is watching for.
/// </remarks>
[NotInParallel]
public sealed class FleetMachineCountLiveTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    /// <summary>Starts Postgres and runs migrations once for the class.</summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _migratedConnectionString = _fixture.ConnectionString;

        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(_migratedConnectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Warning));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    /// <summary>Stops the container after the class.</summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    private static DatabaseContext CreateContext()
    {
        DataOptions<DatabaseContext> options = new(new DataOptions().UsePostgreSQL(_migratedConnectionString));

        return new DatabaseContext(options);
    }

    private static DatabaseRepository CreateRepo(DatabaseContext db) => new(db, NullLogger<DatabaseRepository>.Instance);

    /// <summary>
    /// Clears machines and their summaries before each test. The fixture is shared for the class and
    /// the assertions are exact counts, so a row left by a neighbouring test would fail this one for
    /// a reason unrelated to the query.
    /// </summary>
    private static async Task ClearMachinesAsync(DatabaseContext db)
    {
        await db.MachineStateSummaries.DeleteAsync();
        await db.Machines.DeleteAsync();
    }

    private static async Task<int> SeedTenantAsync(DatabaseContext db)
    {
        return await db.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Fleet Count Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });
    }

    private static async Task<long> SeedMachineAsync(
        DatabaseContext db, int tenantId, short? healthStatus, bool isDeleted = false)
    {
        long registrationTokenId = await db.InsertWithInt64IdentityAsync(new RegistrationToken
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Fleet Count Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            IsRevoked = false,
        });

        long machineId = await db.InsertWithInt64IdentityAsync(new Machine
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = "m",
            SerialNumber = Guid.NewGuid().ToString("N"),
            SystemId = Guid.NewGuid().ToString("N"),
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = registrationTokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = isDeleted,
            TenantId = tenantId,
        });

        // A null status means no summary row at all — a machine that registered and never reported.
        if (healthStatus is not null)
        {
            await db.InsertAsync(new MachineStateSummary
            {
                MachineId = machineId,
                TenantId = tenantId,
                Name = "m",
                OperatingSystem = 0,
                MachineType = 0,
                HealthStatus = healthStatus.Value,
            });
        }

        return machineId;
    }

    [Test]
    public async Task FleetMachineCounts_SumAcrossTenantsAndExcludeDeletedMachines()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        await ClearMachinesAsync(db);

        int firstTenant = await SeedTenantAsync(db);
        int secondTenant = await SeedTenantAsync(db);

        await SeedMachineAsync(db, firstTenant, (short)0);
        await SeedMachineAsync(db, secondTenant, (short)0);
        await SeedMachineAsync(db, secondTenant, (short)1);

        // Not a fleet member. Counting it would inflate the gate the ingest rule depends on.
        await SeedMachineAsync(db, firstTenant, (short)0, isDeleted: true);

        IReadOnlyDictionary<short, int> counts = await repo.GetFleetMachineCountsByHealthAsync(CancellationToken.None);

        await Assert.That(counts.GetValueOrDefault((short)0)).IsEqualTo(2);
        await Assert.That(counts.GetValueOrDefault((short)1)).IsEqualTo(1);
    }

    [Test]
    public async Task FleetMachineCounts_MachineWithNoSummaryRowCountsAsOffline()
    {
        // A machine that registered but never reported telemetry. Dropping it would hide the
        // population the ingest-stalled rule exists to notice.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        await ClearMachinesAsync(db);

        int tenantId = await SeedTenantAsync(db);
        await SeedMachineAsync(db, tenantId, healthStatus: null);

        IReadOnlyDictionary<short, int> counts = await repo.GetFleetMachineCountsByHealthAsync(CancellationToken.None);

        await Assert.That(counts.GetValueOrDefault((short)3)).IsEqualTo(1);
    }

    [Test]
    public async Task FleetMachineCounts_EmptyFleet_ReturnsNoRows()
    {
        // The gauge turns this into an explicit zero per status; the query itself simply has
        // nothing to group.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        await ClearMachinesAsync(db);

        IReadOnlyDictionary<short, int> counts = await repo.GetFleetMachineCountsByHealthAsync(CancellationToken.None);

        await Assert.That(counts.Count).IsEqualTo(0);
    }
}
