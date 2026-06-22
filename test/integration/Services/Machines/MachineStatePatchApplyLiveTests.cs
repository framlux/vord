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
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// Live integration tests for <see cref="IMachineStateRepository.ApplySummaryPatchAsync"/> and
/// <see cref="IMachineStateRepository.ApplyDetailPatchAsync"/> against a real Postgres backend
/// (Testcontainers). The monotonic <c>LastSeenAt</c> guard uses a column-referencing conditional
/// <c>.Set</c> whose SQL translation can differ between providers; the SQLite unit tests cannot
/// prove it works on Postgres, so these tests exercise it against the real engine. Each test uses
/// a unique machine id so the tests remain isolated on the shared container.
/// </summary>
public sealed class MachineStatePatchApplyLiveTests
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

    /// <summary>
    /// Inserts the full Tenant -> RegistrationToken -> Machine chain required by the FK constraints
    /// on the state tables and returns the generated machine id. The system user (Id 1) is seeded by
    /// the initial migration, so CreatedByUserId references resolve. A fresh chain per call keeps each
    /// test isolated on the shared container.
    /// </summary>
    private static async Task<(long MachineId, int TenantId)> SeedMachineAsync(DatabaseContext db)
    {
        int tenantId = await db.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Live Test Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });

        long registrationTokenId = await db.InsertWithInt64IdentityAsync(new RegistrationToken
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Live Test Token",
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

    private static async Task SeedSummaryAsync(DatabaseContext db, long machineId, int tenantId, DateTimeOffset? lastSeenAt)
    {
        await db.InsertAsync(new MachineStateSummary
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = "m",
            LastSeenAt = lastSeenAt,
        });
    }

    private static async Task SeedDetailAsync(DatabaseContext db, long machineId)
    {
        await db.InsertAsync(new MachineStateDetail { MachineId = machineId });
    }

    [Test]
    public async Task ApplySummaryPatch_SetsOnlyOwnedColumns_AndPreservesUntouchedColumns()
    {
        // Intent: a present type writes its owned column while a previously-set value for a different
        // type is left untouched. Proves the per-type .Set selection translates correctly on Postgres.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (long machineId, int tenantId) = await SeedMachineAsync(db);
        await SeedSummaryAsync(db, machineId, tenantId, DateTimeOffset.UnixEpoch.AddHours(5));
        await db.GetTable<MachineStateSummary>().Where(s => s.MachineId == machineId)
            .Set(s => s.MemoryUsagePercent, 11).UpdateAsync();

        MachineSummaryPatch patch = new()
        {
            MachineId = machineId,
            LastSeenAt = DateTimeOffset.UnixEpoch.AddHours(6),
            HasCpuUsage = true,
            CpuUsagePercent = 77,
        };

        await repo.ApplySummaryPatchAsync(patch, CancellationToken.None);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == machineId);
        await Assert.That(s.CpuUsagePercent).IsEqualTo(77);     // written
        await Assert.That(s.MemoryUsagePercent).IsEqualTo(11);  // untouched type preserved
        await Assert.That(s.LastSeenAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddHours(6));
    }

    [Test]
    public async Task ApplySummaryPatch_NeverMovesLastSeenAtBackward_OnPostgres()
    {
        // Intent: stored LastSeenAt newer than the patch value must stay unchanged while the owned
        // column is still written in the same call. This is the conditional column-referencing .Set
        // whose SQL translation is the entire reason this test runs against real Postgres.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (long machineId, int tenantId) = await SeedMachineAsync(db);
        await SeedSummaryAsync(db, machineId, tenantId, DateTimeOffset.UnixEpoch.AddHours(5));

        MachineSummaryPatch patch = new()
        {
            MachineId = machineId,
            LastSeenAt = DateTimeOffset.UnixEpoch.AddHours(1), // older than stored
            HasCpuUsage = true,
            CpuUsagePercent = 5,
        };

        await repo.ApplySummaryPatchAsync(patch, CancellationToken.None);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == machineId);
        await Assert.That(s.CpuUsagePercent).IsEqualTo(5);                               // column still written
        await Assert.That(s.LastSeenAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddHours(5)); // NOT moved backward
    }

    [Test]
    public async Task ApplySummaryPatch_NullStoredLastSeenAt_IsAdvancedToPatchValue_OnPostgres()
    {
        // Intent: a null stored LastSeenAt must advance to the patch value. Proves the null branch of
        // the conditional .Set evaluates correctly on Postgres (NULL comparison semantics).
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (long machineId, int tenantId) = await SeedMachineAsync(db);
        await SeedSummaryAsync(db, machineId, tenantId, null);

        MachineSummaryPatch patch = new()
        {
            MachineId = machineId,
            LastSeenAt = DateTimeOffset.UnixEpoch.AddHours(2),
            HasCpuUsage = true,
            CpuUsagePercent = 9,
        };

        await repo.ApplySummaryPatchAsync(patch, CancellationToken.None);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == machineId);
        await Assert.That(s.LastSeenAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddHours(2));
        await Assert.That(s.CpuUsagePercent).IsEqualTo(9); // owned column written in same call
    }

    [Test]
    public async Task ApplySummaryPatch_OlderStoredLastSeenAt_IsAdvancedToPatchValue_OnPostgres()
    {
        // Intent: a stored LastSeenAt older than the patch value must advance forward. Together with
        // the backward and null cases this proves all three branches of the conditional .Set on Postgres.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (long machineId, int tenantId) = await SeedMachineAsync(db);
        await SeedSummaryAsync(db, machineId, tenantId, DateTimeOffset.UnixEpoch.AddHours(3));

        MachineSummaryPatch patch = new()
        {
            MachineId = machineId,
            LastSeenAt = DateTimeOffset.UnixEpoch.AddHours(8), // newer than stored
            HasCpuUsage = true,
            CpuUsagePercent = 12,
        };

        await repo.ApplySummaryPatchAsync(patch, CancellationToken.None);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == machineId);
        await Assert.That(s.LastSeenAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddHours(8)); // advanced
        await Assert.That(s.CpuUsagePercent).IsEqualTo(12);                              // column written
    }

    [Test]
    public async Task ApplyDetailPatch_SetsOnlyOwnedColumns_OnPostgres()
    {
        // Intent: a present detail type writes its owned (JSONB) column while a previously-set value
        // for a different type is preserved. Proves the per-type detail .Set selection works on Postgres.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (long machineId, int _) = await SeedMachineAsync(db);
        await SeedDetailAsync(db, machineId);
        await db.GetTable<MachineStateDetail>().Where(d => d.MachineId == machineId)
            .Set(d => d.Kernel, "5.15").UpdateAsync();

        MachineDetailPatch patch = new()
        {
            MachineId = machineId,
            HasDiskInfo = true,
            DiskInfos = """[{"name":"sda"}]""",
        };

        await repo.ApplyDetailPatchAsync(patch, CancellationToken.None);

        MachineStateDetail d = await db.GetTable<MachineStateDetail>().FirstAsync(x => x.MachineId == machineId);
        await Assert.That(JsonEquals(d.DiskInfos!, """[{"name":"sda"}]""")).IsTrue(); // semantic JSONB round-trip
        await Assert.That(d.Kernel).IsEqualTo("5.15"); // untouched type preserved
    }

    [Test]
    public async Task ApplyDetailPatch_WithNoDetailTypes_IssuesNoUpdateAndDoesNotThrow_OnPostgres()
    {
        // Intent: HasAnyDetail == false must short-circuit and issue no UPDATE (an UPDATE that sets
        // zero columns is invalid SQL on Postgres), leaving the row untouched and not throwing.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (long machineId, int _) = await SeedMachineAsync(db);
        await SeedDetailAsync(db, machineId);
        await db.GetTable<MachineStateDetail>().Where(d => d.MachineId == machineId)
            .Set(d => d.Kernel, "6.1").UpdateAsync();

        MachineDetailPatch patch = new()
        {
            MachineId = machineId,
            // No presence flags set: HasAnyDetail is false, so no UPDATE should be issued.
        };

        await repo.ApplyDetailPatchAsync(patch, CancellationToken.None);

        MachineStateDetail d = await db.GetTable<MachineStateDetail>().FirstAsync(x => x.MachineId == machineId);
        await Assert.That(d.Kernel).IsEqualTo("6.1"); // untouched, no update issued
    }

    [Test]
    public async Task ApplySummaryPatch_HardwareHealth_PersistsBoolFlags_AndDetailPayload_OnPostgres()
    {
        // Intent: the nullable bool HardwareHealth summary flags are the only Postgres column KIND with
        // no live coverage; persist them as true/false exactly and round-trip the detail HardwareHealth
        // JSONB payload. Proves bool? mapping and the JSONB object column work on real Postgres.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (long machineId, int tenantId) = await SeedMachineAsync(db);
        await SeedSummaryAsync(db, machineId, tenantId, DateTimeOffset.UnixEpoch.AddHours(4));
        await SeedDetailAsync(db, machineId);

        MachineSummaryPatch summaryPatch = new()
        {
            MachineId = machineId,
            LastSeenAt = DateTimeOffset.UnixEpoch.AddHours(7),
            HasHardwareHealth = true,
            HasDiskHealthIssue = true,
            HasHardwareIssue = false,
        };

        await repo.ApplySummaryPatchAsync(summaryPatch, CancellationToken.None);

        MachineDetailPatch detailPatch = new()
        {
            MachineId = machineId,
            HasHardwareHealth = true,
            HardwareHealth = """{"disk_smart":[{"health_status":"FAILED"}]}""",
        };

        await repo.ApplyDetailPatchAsync(detailPatch, CancellationToken.None);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == machineId);
        await Assert.That(s.HasDiskHealthIssue).IsTrue();  // bool? persisted as true
        await Assert.That(s.HasHardwareIssue).IsFalse();   // bool? persisted as false

        MachineStateDetail d = await db.GetTable<MachineStateDetail>().FirstAsync(x => x.MachineId == machineId);
        await Assert.That(JsonEquals(d.HardwareHealth!, """{"disk_smart":[{"health_status":"FAILED"}]}""")).IsTrue();
    }

    [Test]
    public async Task ApplySummaryPatch_SystemInfo_PersistsAllColumns_IncludingJsonbIpAddresses_OnPostgres()
    {
        // Intent: cover the JSONB SUMMARY column (IpAddresses) and the multi-column SystemInfo .Set
        // blocks the existing CpuUsage/DiskInfo tests do not exercise. Assert every column persists
        // with its exact value (semantic compare for the JSONB array).
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (long machineId, int tenantId) = await SeedMachineAsync(db);
        await SeedSummaryAsync(db, machineId, tenantId, DateTimeOffset.UnixEpoch.AddHours(4));
        await SeedDetailAsync(db, machineId);

        MachineSummaryPatch summaryPatch = new()
        {
            MachineId = machineId,
            HasSystemInfo = true,
            Hostname = "live-host-01",
            HardwareModel = "PowerEdge R740",
            IpAddresses = """["10.0.0.1","10.0.0.2"]""",
        };

        await repo.ApplySummaryPatchAsync(summaryPatch, CancellationToken.None);

        MachineDetailPatch detailPatch = new()
        {
            MachineId = machineId,
            HasSystemInfo = true,
            HardwareVendor = "Dell Inc.",
            HardwareSerial = "SN-ABC-123",
            CpuBrand = "Intel Xeon Gold 6248",
            CpuCores = 20,
            MemoryTotalBytes = 137438953472L,
            UptimeSeconds = 864000L,
            BiosVersion = "2.10.2",
        };

        await repo.ApplyDetailPatchAsync(detailPatch, CancellationToken.None);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == machineId);
        await Assert.That(s.Hostname).IsEqualTo("live-host-01");
        await Assert.That(s.HardwareModel).IsEqualTo("PowerEdge R740");
        await Assert.That(JsonEquals(s.IpAddresses!, """["10.0.0.1","10.0.0.2"]""")).IsTrue();

        MachineStateDetail d = await db.GetTable<MachineStateDetail>().FirstAsync(x => x.MachineId == machineId);
        await Assert.That(d.HardwareVendor).IsEqualTo("Dell Inc.");
        await Assert.That(d.HardwareSerial).IsEqualTo("SN-ABC-123");
        await Assert.That(d.CpuBrand).IsEqualTo("Intel Xeon Gold 6248");
        await Assert.That(d.CpuCores).IsEqualTo(20);
        await Assert.That(d.MemoryTotalBytes).IsEqualTo(137438953472L);
        await Assert.That(d.UptimeSeconds).IsEqualTo(864000L);
        await Assert.That(d.BiosVersion).IsEqualTo("2.10.2");
    }

    [Test]
    public async Task ApplySummaryPatch_NonExistentMachine_IsNoOpAndDoesNotThrow_OnPostgres()
    {
        // Intent: applying a summary patch to a machine with no summary row must be a 0-row UPDATE
        // no-op that neither throws nor inserts. Locks the "UPDATE ... WHERE no match" behavior.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        long absentId = long.MaxValue - 12345;

        MachineSummaryPatch patch = new()
        {
            MachineId = absentId,
            LastSeenAt = DateTimeOffset.UnixEpoch.AddHours(1),
            HasCpuUsage = true,
            CpuUsagePercent = 42,
        };

        await repo.ApplySummaryPatchAsync(patch, CancellationToken.None);

        int rowCount = await db.GetTable<MachineStateSummary>().CountAsync(x => x.MachineId == absentId);
        await Assert.That(rowCount).IsEqualTo(0); // no insert occurred
    }

    /// <summary>
    /// Compares two JSON documents for semantic equality, canonicalizing each via a round-trip through
    /// <see cref="JsonNode"/> so Postgres' jsonb whitespace and key ordering do not affect the result.
    /// </summary>
    private static bool JsonEquals(string a, string b)
    {
        string canonicalA = JsonSerializer.Serialize(JsonNode.Parse(a));
        string canonicalB = JsonSerializer.Serialize(JsonNode.Parse(b));

        return string.Equals(canonicalA, canonicalB, StringComparison.Ordinal);
    }
}
