// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// Boundary coverage for the health rule as PostgreSQL actually evaluates it.
/// </summary>
/// <remarks>
/// The read paths no longer compute health; they surface the column this sweep writes, so
/// <see cref="PostgresSqlDialect.HealthSweepForTenant"/> is the only definition of the rule that
/// reaches production. The equivalent boundary matrix in the unit suite runs against the SQLite
/// dialect in the test harness, which is a second hand-written copy — if the two drift, those
/// tests stay green while production is wrong. These cases close that gap by exercising the
/// production SQL against a real Postgres.
/// </remarks>
public sealed class HealthSweepThresholdLiveTests
{
    private const int OnlineThresholdSeconds = 300;

    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    /// <summary>
    /// Starts the Postgres container once and runs migrations so the schema is ready.
    /// </summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _migratedConnectionString = _fixture.ConnectionString;
        await RunMigrationsAsync(_migratedConnectionString);
    }

    /// <summary>
    /// Stops the Postgres container after all tests in the class.
    /// </summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
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

    private static DatabaseRepository CreateRepo(DatabaseContext db)
    {
        return new DatabaseRepository(db, NullLogger<DatabaseRepository>.Instance);
    }

    private static async Task<int> SeedTenantAsync(DatabaseContext db)
    {
        return await db.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Health Sweep Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });
    }

    private static async Task<long> SeedSummaryAsync(
        DatabaseContext db,
        int tenantId,
        int? cpu = null,
        int? memory = null,
        int? maxDisk = null,
        int failedServices = 0,
        bool diskIssue = false,
        bool hardwareIssue = false,
        DateTimeOffset? lastSeenAt = null)
    {
        long registrationTokenId = await db.InsertWithInt64IdentityAsync(new RegistrationToken
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Health Sweep Token",
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
            IsDeleted = false,
            TenantId = tenantId,
        });

        await db.InsertAsync(new MachineStateSummary
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = "m",
            Hostname = "m",
            OperatingSystem = 0,
            MachineType = 0,
            CpuUsagePercent = cpu,
            MemoryUsagePercent = memory,
            MaxDiskUsagePercent = maxDisk,
            FailedServices = failedServices,
            HasDiskHealthIssue = diskIssue,
            HasHardwareIssue = hardwareIssue,
            // Seeded to a value the rule will overwrite, so a sweep that silently did nothing
            // cannot be mistaken for a sweep that computed the expected answer.
            HealthStatus = 1,
            LastSeenAt = lastSeenAt ?? DateTimeOffset.UtcNow,
        });

        return machineId;
    }

    private static async Task SweepAsync(DatabaseRepository repo, int tenantId)
    {
        await repo.SweepHealthStatusAsync(
            new PostgresSqlDialect().HealthSweepForTenant,
            tenantId,
            OnlineThresholdSeconds,
            HealthRuleCases.StaleSeconds,
            CancellationToken.None);
    }

    private static async Task<short> ReadHealthAsync(DatabaseContext db, long machineId)
    {
        MachineStateSummary summary = await db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .FirstAsync();

        return summary.HealthStatus;
    }

    [Test]
    [MethodDataSource(typeof(HealthRuleCases), nameof(HealthRuleCases.All))]
    public async Task Sweep_AtEveryThresholdBoundary_WritesTheExpectedStatus(HealthRuleCase testCase)
    {
        short swept = await HealthRuleEvaluation.EvaluateInPostgresAsync(_migratedConnectionString, testCase);

        await Assert.That(swept).IsEqualTo(testCase.Expected);
    }

    [Test]
    public async Task Sweep_LeavesOtherTenantsUntouched()
    {
        using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int sweptTenant = await SeedTenantAsync(db);
        int otherTenant = await SeedTenantAsync(db);

        long sweptMachine = await SeedSummaryAsync(db, sweptTenant, cpu: 10, memory: 10, maxDisk: 10);
        long otherMachine = await SeedSummaryAsync(db, otherTenant, cpu: 10, memory: 10, maxDisk: 10);

        await SweepAsync(repo, sweptTenant);

        // Both seed at 1 and would compute to 0. Only the swept tenant's row may move.
        await Assert.That(await ReadHealthAsync(db, sweptMachine)).IsEqualTo((short)0);
        await Assert.That(await ReadHealthAsync(db, otherMachine)).IsEqualTo((short)1);
    }
}
