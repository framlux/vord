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
using Framlux.FleetManagement.Services.Core.Machines.Projection;
using Framlux.FleetManagement.Test.Integration;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// Live end-to-end test against real Postgres (Testcontainers) proving that machine recency —
/// specifically the MachineOffline sweep — derives from the server-stamped <c>ServerReceivedAt</c>
/// column and never from the agent's collected-at clock. A forward-skewed agent whose telemetry
/// carries a future <c>ReceivedAt</c> but an old <c>ServerReceivedAt</c> must still be swept offline.
/// This is the guard that fails if anyone re-derives LastSeenAt from agent time.
/// </summary>
public sealed class OfflineSweepServerReceiptLiveTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    /// <summary>
    /// Starts the Postgres container once and runs migrations so the schema is ready for all tests.
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

    private static async Task<(long MachineId, int TenantId)> SeedMachineAsync(DatabaseContext db)
    {
        int tenantId = await db.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Offline Sweep Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });

        long registrationTokenId = await db.InsertWithInt64IdentityAsync(new RegistrationToken
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Offline Sweep Token",
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

        return (machineId, tenantId);
    }

    /// <summary>
    /// Drops the sub-microsecond component of an instant so it survives a PostgreSQL timestamptz
    /// round trip unchanged. Without this, a value written and read back is not equal to the one
    /// held in memory.
    /// </summary>
    /// <param name="value">The instant to truncate.</param>
    /// <returns>The instant with microsecond precision.</returns>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    [Test]
    public async Task OfflineSweep_LastSeenDerivesFromServerReceipt_MarksSkewedDeadAgentOffline()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (long machineId, int tenantId) = await SeedMachineAsync(db);

        // Fresh summary: never seen, so the monotonic apply is free to set LastSeenAt from the patch.
        await db.InsertAsync(new MachineStateSummary
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = "m",
            LastSeenAt = null,
            HealthStatus = 0,
        });

        // Truncated to microseconds because PostgreSQL timestamptz stores microsecond precision
        // while DateTimeOffset counts 100ns ticks. Writing a raw UtcNow and then asserting exact
        // equality against it compares a value that was truncated on the way into the database
        // with one that was not, so the assertion failed for roughly nine runs in ten — and the
        // failure printed identical expected and received values, because they differ below the
        // displayed precision. The sweep still needs a real clock (it is compared against
        // Postgres NOW()), so the instant stays live; only its precision is aligned.
        DateTimeOffset now = TruncateToMicroseconds(DateTimeOffset.UtcNow);

        // The agent's clock is three days fast (ReceivedAt in the future), but the server actually
        // received these rows well beyond the offline threshold ago. A full day back keeps the machine
        // decisively offline against Postgres NOW() even if the container clock is skewed from the host.
        MachineTelemetry skewed1 = new()
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = 6,
            Payload = """{ "cpu_usage_percent": 10 }""",
            ReceivedAt = now.AddDays(3),
            ServerReceivedAt = now.AddDays(-1),
            SourceEventId = Guid.NewGuid().ToString("N"),
        };
        MachineTelemetry skewed2 = new()
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = 7,
            Payload = """{ "memory_usage_percent": 20 }""",
            ReceivedAt = now.AddDays(3).AddMinutes(1),
            ServerReceivedAt = now.AddDays(-1).AddMinutes(1),
            SourceEventId = Guid.NewGuid().ToString("N"),
        };
        await db.InsertAsync(skewed1);
        await db.InsertAsync(skewed2);

        // Project the rows exactly as the streaming service does: collapse, then apply the summary.
        List<MachineTelemetry> batch = await db.GetTable<MachineTelemetry>()
            .Where(t => t.MachineId == machineId)
            .ToListAsync();
        CollapseResult collapse = MachineStateBatchCollapser.Collapse(batch);
        MachineStatePatch statePatch = collapse.Patches.Single();

        await repo.ApplySummaryPatchAsync(
            new MachineSummaryPatch { MachineId = machineId, LastSeenAt = statePatch.LastSeenAt },
            CancellationToken.None);

        // The projected LastSeenAt must be the max server receipt (a day ago), not the future agent time.
        MachineStateSummary projected = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == machineId);
        await Assert.That(projected.LastSeenAt).IsEqualTo(now.AddDays(-1).AddMinutes(1));

        // Run the real per-tenant offline sweep with a 5-minute threshold.
        await repo.SweepHealthStatusAsync(
            new PostgresSqlDialect().HealthSweepForTenant,
            tenantId,
            onlineThresholdSeconds: 300,
            CancellationToken.None);

        MachineStateSummary swept = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == machineId);

        // Had LastSeenAt been derived from the future agent clock, the machine would read online (0);
        // deriving it from ServerReceivedAt marks it Offline (3).
        await Assert.That(swept.HealthStatus).IsEqualTo((short)3);
    }
}
