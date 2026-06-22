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
