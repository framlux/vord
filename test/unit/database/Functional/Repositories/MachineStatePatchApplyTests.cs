// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.Repositories;

/// <summary>
/// Tests for the combined patch-apply repository methods. Verifies exactly the targeted
/// columns are written, untouched types preserve prior values, and LastSeenAt is monotonic.
/// </summary>
public class MachineStatePatchApplyTests
{
    private static Database.Repositories.DatabaseRepository BuildRepository(TestDatabaseFactory dbFactory) =>
        new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

    private static async Task SeedAsync(DatabaseContext db, long machineId)
    {
        await db.InsertAsync(new MachineStateSummary { MachineId = machineId, TenantId = 1, Name = "m", LastSeenAt = DateTimeOffset.UnixEpoch.AddHours(5) });
        await db.InsertAsync(new MachineStateDetail { MachineId = machineId });
    }

    [Test]
    public async Task ApplySummaryPatch_SetsOnlyOwnedColumns_AndPreservesUntouchedColumns()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedAsync(db, 100);
        await db.GetTable<MachineStateSummary>().Where(s => s.MachineId == 100)
            .Set(s => s.MemoryUsagePercent, 11).UpdateAsync();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        MachineSummaryPatch patch = new()
        {
            MachineId = 100,
            LastSeenAt = DateTimeOffset.UnixEpoch.AddHours(6),
            HasCpuUsage = true,
            CpuUsagePercent = 77,
        };

        await repo.ApplySummaryPatchAsync(patch, CancellationToken.None);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == 100);
        await Assert.That(s.CpuUsagePercent).IsEqualTo(77);     // written
        await Assert.That(s.MemoryUsagePercent).IsEqualTo(11);  // untouched type preserved
        await Assert.That(s.LastSeenAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddHours(6));
    }

    [Test]
    public async Task ApplySummaryPatch_HardwareHealthWithWear_WritesTheWearColumn()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedAsync(db, 140);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        MachineSummaryPatch patch = new()
        {
            MachineId = 140,
            HasHardwareHealth = true,
            HasDiskHealthIssue = false,
            HasHardwareIssue = false,
            MaxDiskWearoutPercent = 84,
        };

        await repo.ApplySummaryPatchAsync(patch, CancellationToken.None);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == 140);
        await Assert.That(s.MaxDiskWearoutPercent).IsEqualTo(84);
    }

    [Test]
    public async Task ApplySummaryPatch_HardwareHealthWithoutWear_LeavesAStoredWearValueAlone()
    {
        // smartctl exits non-zero — and so contributes no disk entry at all — exactly when a drive
        // is failing, so a report carrying no usable wear figure must not erase a known one. Drop
        // the non-null guard in the repository and this test fails: the machine silently returns to
        // Healthy through the very failure the column exists to surface.
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedAsync(db, 141);
        await db.GetTable<MachineStateSummary>().Where(s => s.MachineId == 141)
            .Set(s => s.MaxDiskWearoutPercent, 96).UpdateAsync();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        MachineSummaryPatch patch = new()
        {
            MachineId = 141,
            HasHardwareHealth = true,
            HasDiskHealthIssue = true,
            HasHardwareIssue = false,
            MaxDiskWearoutPercent = null,
        };

        await repo.ApplySummaryPatchAsync(patch, CancellationToken.None);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == 141);
        await Assert.That(s.MaxDiskWearoutPercent).IsEqualTo(96);
        await Assert.That(s.HasDiskHealthIssue).IsTrue();
    }

    [Test]
    public async Task ApplySummaryPatch_NoHardwareHealthAtAll_LeavesTheWearColumnAlone()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedAsync(db, 142);
        await db.GetTable<MachineStateSummary>().Where(s => s.MachineId == 142)
            .Set(s => s.MaxDiskWearoutPercent, 55).UpdateAsync();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        MachineSummaryPatch patch = new()
        {
            MachineId = 142,
            HasCpuUsage = true,
            CpuUsagePercent = 12,
        };

        await repo.ApplySummaryPatchAsync(patch, CancellationToken.None);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == 142);
        await Assert.That(s.MaxDiskWearoutPercent).IsEqualTo(55);
    }

    [Test]
    public async Task ApplySummaryPatch_NeverMovesLastSeenAtBackward()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedAsync(db, 100); // stored LastSeenAt = epoch+5h
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        MachineSummaryPatch patch = new()
        {
            MachineId = 100,
            LastSeenAt = DateTimeOffset.UnixEpoch.AddHours(1), // older than stored
            HasCpuUsage = true,
            CpuUsagePercent = 5,
        };

        await repo.ApplySummaryPatchAsync(patch, CancellationToken.None);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == 100);
        await Assert.That(s.CpuUsagePercent).IsEqualTo(5);                               // column still written
        await Assert.That(s.LastSeenAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddHours(5)); // NOT moved backward
    }

    [Test]
    public async Task ApplySummaryPatch_NullStoredLastSeenAt_IsAdvancedToPatchValue()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await db.InsertAsync(new MachineStateSummary { MachineId = 100, TenantId = 1, Name = "m", LastSeenAt = null });
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        MachineSummaryPatch patch = new()
        {
            MachineId = 100,
            LastSeenAt = DateTimeOffset.UnixEpoch.AddHours(2),
            HasCpuUsage = true,
            CpuUsagePercent = 9,
        };

        await repo.ApplySummaryPatchAsync(patch, CancellationToken.None);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == 100);
        await Assert.That(s.LastSeenAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddHours(2));
    }

    [Test]
    public async Task ApplyDetailPatch_SetsOnlyOwnedColumns()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedAsync(db, 100);
        await db.GetTable<MachineStateDetail>().Where(d => d.MachineId == 100)
            .Set(d => d.Kernel, "5.15").UpdateAsync();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        MachineDetailPatch patch = new()
        {
            MachineId = 100,
            HasDiskInfo = true,
            DiskInfos = """[{"name":"sda"}]""",
        };

        await repo.ApplyDetailPatchAsync(patch, CancellationToken.None);

        MachineStateDetail d = await db.GetTable<MachineStateDetail>().FirstAsync(x => x.MachineId == 100);
        await Assert.That(d.DiskInfos).IsEqualTo("""[{"name":"sda"}]""");
        await Assert.That(d.Kernel).IsEqualTo("5.15"); // untouched type preserved
    }

    [Test]
    public async Task ApplyDetailPatch_WithAgentVersion_WritesTheAgentVersionColumn()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedAsync(db, 100);
        await db.GetTable<MachineStateDetail>().Where(d => d.MachineId == 100)
            .Set(d => d.Kernel, "5.15").UpdateAsync();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        MachineDetailPatch patch = new()
        {
            MachineId = 100,
            HasAgentVersion = true,
            AgentVersion = "1.16.0",
        };

        await repo.ApplyDetailPatchAsync(patch, CancellationToken.None);

        MachineStateDetail d = await db.GetTable<MachineStateDetail>().FirstAsync(x => x.MachineId == 100);
        await Assert.That(d.AgentVersion).IsEqualTo("1.16.0");
        await Assert.That(d.Kernel).IsEqualTo("5.15"); // untouched type preserved
    }

    [Test]
    public async Task ApplyDetailPatch_WithoutAgentVersion_PreservesTheRecordedAgentVersion()
    {
        // Intent: a batch that carries no agent version must leave the version already recorded in
        // place. If the presence flag were ignored the column would be nulled on every other batch.
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedAsync(db, 100);
        await db.GetTable<MachineStateDetail>().Where(d => d.MachineId == 100)
            .Set(d => d.AgentVersion, "1.15.3").UpdateAsync();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        MachineDetailPatch patch = new()
        {
            MachineId = 100,
            HasDiskInfo = true,
            DiskInfos = """[{"name":"sda"}]""",
        };

        await repo.ApplyDetailPatchAsync(patch, CancellationToken.None);

        MachineStateDetail d = await db.GetTable<MachineStateDetail>().FirstAsync(x => x.MachineId == 100);
        await Assert.That(d.AgentVersion).IsEqualTo("1.15.3");
    }

    [Test]
    public async Task ApplyDetailPatch_WithOnlyAgentVersion_IsTreatedAsADetailChange()
    {
        // Intent: HasAnyDetail must count the agent version, otherwise a batch carrying only the
        // agent version would be short-circuited and the column would never be written.
        MachineDetailPatch patch = new()
        {
            MachineId = 100,
            HasAgentVersion = true,
            AgentVersion = "1.16.0",
        };

        await Assert.That(patch.HasAnyDetail).IsTrue();
    }

    [Test]
    public async Task ApplyDetailPatch_WithNoDetailTypes_IssuesNoUpdateAndDoesNotThrow()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedAsync(db, 100);
        await db.GetTable<MachineStateDetail>().Where(d => d.MachineId == 100)
            .Set(d => d.Kernel, "6.1").UpdateAsync();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        MachineDetailPatch patch = new()
        {
            MachineId = 100,
            // No presence flags set: HasAnyDetail is false, so no UPDATE should be issued.
        };

        await repo.ApplyDetailPatchAsync(patch, CancellationToken.None);

        MachineStateDetail d = await db.GetTable<MachineStateDetail>().FirstAsync(x => x.MachineId == 100);
        await Assert.That(d.Kernel).IsEqualTo("6.1"); // untouched, no update issued
    }
}
