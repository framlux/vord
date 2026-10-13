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
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Machines.Projection;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// Live test, against real Postgres, that a telemetry value Postgres would refuse cannot stall the
/// machine-state projection. Postgres rejects an over-long <c>varchar(n)</c> value and a NUL in any
/// text value, SQLite accepts both, and the projection retries a failing batch forever, so one
/// machine reporting a long hardware model would freeze the state of every machine in its shard. The
/// values are cleaned when the payload is parsed, and these tests run the real projection service to
/// prove the cleaning is enough.
/// </summary>
[NotInParallel]
public sealed class BoundedSummaryStringsLiveTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The bounded columns the projection writes from agent-reported text, with the limit it cuts each to.</summary>
    private static readonly (string Table, string Column, int Limit)[] ClampedColumns =
    [
        ("MachineStateSummary", "Hostname", MachineStateColumnLimits.Hostname),
        ("MachineStateSummary", "HardwareModel", MachineStateColumnLimits.HardwareModel),
        ("MachineStateSummary", "OsName", MachineStateColumnLimits.OsName),
        ("MachineStateSummary", "OsVersion", MachineStateColumnLimits.OsVersion),
        ("MachineStateDetail", "HardwareVendor", MachineStateColumnLimits.HardwareVendor),
        ("MachineStateDetail", "HardwareSerial", MachineStateColumnLimits.HardwareSerial),
        ("MachineStateDetail", "CpuBrand", MachineStateColumnLimits.CpuBrand),
        ("MachineStateDetail", "BiosVersion", MachineStateColumnLimits.BiosVersion),
        ("MachineStateDetail", "Kernel", MachineStateColumnLimits.Kernel),
        ("MachineStateDetail", "CpuType", MachineStateColumnLimits.CpuType),
        ("MachineStateDetail", "AgentVersion", MachineStateColumnLimits.AgentVersion),
    ];

    /// <summary>
    /// Bounded columns the projection does not write. MachineStateSummary.Name is written at
    /// registration and by a rename, not by telemetry, and a rename is refused above 250 characters.
    /// </summary>
    private static readonly (string Table, string Column)[] ExemptColumns =
    [
        ("MachineStateSummary", "Name"),
    ];

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

    private static async Task<long> SeedMachineAsync(DatabaseContext db)
    {
        int tenantId = await db.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Bounded Strings Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });

        long registrationTokenId = await db.InsertWithInt64IdentityAsync(new RegistrationToken
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Bounded Strings Token",
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
            MachineType = MachineTypes.SingleBoardComputer,
            OperatingSystem = OperatingSystems.Debian,
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
            MachineType = (byte)MachineTypes.SingleBoardComputer,
            HealthStatus = 3,
        });
        await db.InsertAsync(new MachineStateDetail { MachineId = machineId });

        return machineId;
    }

    private static async Task<long> InsertTelemetryAsync(DatabaseContext db, long machineId, short telemetryType, object payload)
    {
        int tenantId = await db.GetTable<Machine>().Where(m => m.Id == machineId).Select(m => m.TenantId).FirstAsync();

        return await db.InsertWithInt64IdentityAsync(new MachineTelemetry
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = telemetryType,
            Payload = JsonSerializer.Serialize(payload),
            ReceivedAt = Now.AddMinutes(-10),
            ServerReceivedAt = Now.AddMinutes(-10),
            SourceEventId = Guid.NewGuid().ToString("N"),
        });
    }

    /// <summary>
    /// Runs the real projection service over everything pending until it has finished its first
    /// batch, and returns the failure the batch hit, if any. Completion is gated on the loop's second
    /// poll, which only happens after the cursor has been written, or on the first failed write, so
    /// the wait depends on neither wall-clock time nor a retry that would never come.
    /// </summary>
    private static async Task<Exception?> ProjectPendingBatchAsync()
    {
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        int polls = 0;

        ServiceCollection services = new();
        services.AddScoped(_ => CreateContext());
        services.AddScoped<IMachineStateRepository>(sp => ForwardingMachineStateRepository.Create(
            new DatabaseRepository(sp.GetRequiredService<DatabaseContext>(), NullLogger<DatabaseRepository>.Instance),
            onPoll: () =>
            {
                if (Interlocked.Increment(ref polls) == 2)
                {
                    finished.TrySetResult();
                }
            },
            onFailure: exception =>
            {
                failure = exception;
                finished.TrySetResult();
            }));

        await using ServiceProvider provider = services.BuildServiceProvider();

        MachineStateStreamingService service = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new PostgresSqlDialect(),
            new NoOpAdvisoryLockProvider(),
            NullLogger<MachineStateStreamingService>.Instance,
            shardIndex: 0,
            Microsoft.Extensions.Options.Options.Create(new StreamingOptions { ShardCount = 1, BatchSize = 200, VisibilityLagSeconds = 0 }),
            TestMetricsFactory.CreateProjectionMetrics(),
            timeProvider: new FakeTimeProvider(Now),
            startupDelay: TimeSpan.Zero);

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);

        await finished.Task;

        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        return failure;
    }

    [Test]
    public async Task OverLongValues_AreStoredTruncated_AndTheProjectionAdvances()
    {
        // Every bounded string the projection writes arrives longer than its column. Before the cut
        // the first UPDATE failed with "value too long", the batch was retried for ever, and the
        // cursor never moved.
        await using DatabaseContext db = CreateContext();
        long machineId = await SeedMachineAsync(db);

        long systemInfoId = await InsertTelemetryAsync(db, machineId, TelemetryTypeIds.SystemInfo, new
        {
            hostname = new string('h', 300),
            hardware_model = new string('m', 400),
            hardware_vendor = new string('v', 300),
            hardware_serial = new string('s', 300),
            cpu_brand = new string('c', 300),
            bios_version = new string('b', 100),
            ip_addresses = new[] { "10.0.0.2" },
            cpu_physical_cores = 4,
            physical_memory = 4_000_000_000L,
            uptime_seconds = 1234L,
        });
        long osVersionId = await InsertTelemetryAsync(db, machineId, TelemetryTypeIds.OsVersion, new
        {
            name = new string('n', 300),
            version = new string('x', 100),
            build = new string('k', 300),
        });
        long cpuInfoId = await InsertTelemetryAsync(db, machineId, TelemetryTypeIds.CpuInfo, new
        {
            processor_type = new string('t', 100),
            number_of_cores = "4",
            logical_processors = 4,
        });
        long agentVersionId = await InsertTelemetryAsync(db, machineId, TelemetryTypeIds.AgentVersion, new
        {
            version = new string('a', 100),
        });

        Exception? failure = await ProjectPendingBatchAsync();

        await Assert.That(failure).IsNull().Because($"the projection failed: {failure}");

        MachineStateSummary summary = await db.GetTable<MachineStateSummary>().FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.Hostname).IsEqualTo(new string('h', 255));
        await Assert.That(summary.HardwareModel).IsEqualTo(new string('m', 255));
        await Assert.That(summary.OsName).IsEqualTo(new string('n', 255));
        await Assert.That(summary.OsVersion).IsEqualTo(new string('x', 64));

        MachineStateDetail detail = await db.GetTable<MachineStateDetail>().FirstAsync(d => d.MachineId == machineId);
        await Assert.That(detail.HardwareVendor).IsEqualTo(new string('v', 255));
        await Assert.That(detail.HardwareSerial).IsEqualTo(new string('s', 255));
        await Assert.That(detail.CpuBrand).IsEqualTo(new string('c', 255));
        await Assert.That(detail.BiosVersion).IsEqualTo(new string('b', 64));
        await Assert.That(detail.Kernel).IsEqualTo(new string('k', 255));
        await Assert.That(detail.CpuType).IsEqualTo(new string('t', 64));
        await Assert.That(detail.AgentVersion).IsEqualTo(new string('a', 64));

        IMachineStateRepository repository = new DatabaseRepository(db, NullLogger<DatabaseRepository>.Instance);
        long? cursor = await repository.GetProjectionCursorAsync(0);
        await Assert.That(cursor).IsEqualTo(new[] { systemInfoId, osVersionId, cpuInfoId, agentVersionId }.Max());
    }

    [Test]
    public async Task NulCharacters_NeverReachPostgres_AndTheProjectionAdvances()
    {
        // Postgres refuses a NUL in any text value (22021), and the projection retries a failing
        // batch for ever, so one report carrying a NUL would freeze the shard as surely as an
        // over-long one. Every bounded string carries one here, because a single survivor fails the
        // whole batch. The hostname is long enough that cutting before stripping would leave half of it.
        await using DatabaseContext db = CreateContext();
        long machineId = await SeedMachineAsync(db);

        long systemInfoId = await InsertTelemetryAsync(db, machineId, TelemetryTypeIds.SystemInfo, new
        {
            hostname = string.Concat(Enumerable.Repeat("h\0", 300)),
            hardware_model = "Raspberry\0 Pi 4 Model B\0 Rev 1.4\0",
            hardware_vendor = "Raspberry\0 Pi",
            hardware_serial = "10000000\0a1b2c3d4",
            cpu_brand = "Cortex\0-A72",
            bios_version = "2024.\u00001",
        });
        long osVersionId = await InsertTelemetryAsync(db, machineId, TelemetryTypeIds.OsVersion, new
        {
            name = "Debian\0 GNU/Linux",
            version = "12\0",
            build = "6.1.0\0-rpi",
        });
        long cpuInfoId = await InsertTelemetryAsync(db, machineId, TelemetryTypeIds.CpuInfo, new
        {
            processor_type = "ARMv8\0",
            number_of_cores = "4",
            logical_processors = 4,
        });
        long agentVersionId = await InsertTelemetryAsync(db, machineId, TelemetryTypeIds.AgentVersion, new
        {
            version = "3.4.0\0",
        });

        Exception? failure = await ProjectPendingBatchAsync();

        await Assert.That(failure).IsNull().Because($"the projection failed: {failure}");

        MachineStateSummary summary = await db.GetTable<MachineStateSummary>().FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.Hostname).IsEqualTo(new string('h', 255));
        await Assert.That(summary.HardwareModel).IsEqualTo("Raspberry Pi 4 Model B Rev 1.4");
        await Assert.That(summary.OsName).IsEqualTo("Debian GNU/Linux");
        await Assert.That(summary.OsVersion).IsEqualTo("12");

        MachineStateDetail detail = await db.GetTable<MachineStateDetail>().FirstAsync(d => d.MachineId == machineId);
        await Assert.That(detail.HardwareVendor).IsEqualTo("Raspberry Pi");
        await Assert.That(detail.HardwareSerial).IsEqualTo("10000000a1b2c3d4");
        await Assert.That(detail.CpuBrand).IsEqualTo("Cortex-A72");
        await Assert.That(detail.BiosVersion).IsEqualTo("2024.1");
        await Assert.That(detail.Kernel).IsEqualTo("6.1.0-rpi");
        await Assert.That(detail.CpuType).IsEqualTo("ARMv8");
        await Assert.That(detail.AgentVersion).IsEqualTo("3.4.0");

        IMachineStateRepository repository = new DatabaseRepository(db, NullLogger<DatabaseRepository>.Instance);
        long? cursor = await repository.GetProjectionCursorAsync(0);
        await Assert.That(cursor).IsEqualTo(new[] { systemInfoId, osVersionId, cpuInfoId, agentVersionId }.Max());
    }

    [Test]
    public async Task SurrogatePairAtTheCut_IsDroppedWhole_AndTheProjectionAdvances()
    {
        // Postgres counts characters, so a pair that straddles the cut must go whole: half of it
        // cannot be encoded, and the write would fail like an over-long value does.
        await using DatabaseContext db = CreateContext();
        long machineId = await SeedMachineAsync(db);

        await InsertTelemetryAsync(db, machineId, TelemetryTypeIds.SystemInfo, new
        {
            hardware_model = new string('m', 254) + "\U0001F600",
        });

        Exception? failure = await ProjectPendingBatchAsync();

        await Assert.That(failure).IsNull().Because($"the projection failed: {failure}");

        MachineStateSummary summary = await db.GetTable<MachineStateSummary>().FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.HardwareModel).IsEqualTo(new string('m', 254));
    }

    [Test]
    public async Task ColumnLimits_MatchTheColumnsInThePostgresSchema()
    {
        // SQLite ignores lengths, so only the live schema can say whether a limit has drifted: a
        // limit above its column lets a long value fail the write again, and one below it cuts text
        // the column would have held.
        foreach ((string table, string column, int limit) in ClampedColumns)
        {
            await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(
                "SELECT character_maximum_length FROM information_schema.columns WHERE table_name = @table AND column_name = @column");
            command.Parameters.AddWithValue("table", table);
            command.Parameters.AddWithValue("column", column);

            object? length = await command.ExecuteScalarAsync();

            await Assert.That(length).IsEqualTo(limit).Because($"{table}.{column} is varchar({length}) but the projection cuts to {limit}");
        }
    }

    [Test]
    public async Task EveryBoundedColumn_IsEitherCutByTheProjectionOrExplicitlyExempt()
    {
        // The test above checks the columns the projection knows about. This one asks the schema for
        // every length-bounded column of the two tables it writes, so a column added later fails
        // here until it is given a limit or is exempted on purpose.
        List<string> bounded = [];

        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_name IN ('MachineStateSummary', 'MachineStateDetail')
              AND character_maximum_length IS NOT NULL
            """);

        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                bounded.Add(reader.GetString(0));
            }
        }

        string[] covered = ClampedColumns
            .Select(c => $"{c.Table}.{c.Column}")
            .Concat(ExemptColumns.Select(c => $"{c.Table}.{c.Column}"))
            .ToArray();

        await Assert.That(bounded.Order().ToArray()).IsEquivalentTo(covered.Order().ToArray())
            .Because("a bounded column the projection does not cut, and nothing exempts, can stall it again");
    }
}
