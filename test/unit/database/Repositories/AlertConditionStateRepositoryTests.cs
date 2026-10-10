// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Repositories;

public sealed class AlertConditionStateRepositoryTests
{
    /// <summary>
    /// The gap the evaluation job passes: three one-minute passes. The repository takes it as an argument,
    /// so the tests state it rather than reading the job's constant.
    /// </summary>
    private static readonly TimeSpan ObservationGap = TimeSpan.FromMinutes(3);

    private static IAlertConditionStateRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }

    [Test]
    public async Task GetAsync_NoRow_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        AlertConditionState? result = await repo.GetAsync(alertRuleId: 1, machineId: 1, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetAsync_RowExists_ReturnsRow()
    {
        using TestDatabaseFactory dbFactory = new();
        DateTimeOffset triggeredAt = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = 5,
            MachineId = 17,
            FirstTriggeredAt = triggeredAt,
            LastObservedAt = triggeredAt,
        });

        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        AlertConditionState? result = await repo.GetAsync(alertRuleId: 5, machineId: 17, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.AlertRuleId).IsEqualTo(5);
        await Assert.That(result.MachineId).IsEqualTo(17L);
        await Assert.That(result.FirstTriggeredAt).IsEqualTo(triggeredAt);
    }

    [Test]
    public async Task UpsertObservationAsync_NoExistingRow_InsertsWithFirstAndLastSetToNow()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = new(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

        DateTimeOffset firstTriggered = await repo.UpsertObservationAsync(alertRuleId: 1, machineId: 99, now, ObservationGap, CancellationToken.None);

        await Assert.That(firstTriggered).IsEqualTo(now);

        AlertConditionState? row = await dbFactory.Context.AlertConditionStates
            .Where(s => (s.AlertRuleId == 1) && (s.MachineId == 99))
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.FirstTriggeredAt).IsEqualTo(now);
        await Assert.That(row.LastObservedAt).IsEqualTo(now);
    }

    [Test]
    public async Task UpsertObservationAsync_ExistingRow_PreservesFirstTriggeredAndUpdatesLastObserved()
    {
        // Intent: the duration-window check in AlertEvaluationJob measures elapsed time from
        // FirstTriggeredAt. The upsert path MUST NOT overwrite that on subsequent observations,
        // or the duration window would reset every cycle and alerts would never fire. The prior
        // observation is one pass earlier, so the condition has held continuously.
        using TestDatabaseFactory dbFactory = new();
        DateTimeOffset firstAt = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        DateTimeOffset lateNow = new(2026, 3, 1, 8, 10, 0, TimeSpan.Zero);
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = 7,
            MachineId = 42,
            FirstTriggeredAt = firstAt,
            LastObservedAt = lateNow.AddMinutes(-1),
        });

        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        DateTimeOffset returned = await repo.UpsertObservationAsync(alertRuleId: 7, machineId: 42, lateNow, ObservationGap, CancellationToken.None);

        await Assert.That(returned).IsEqualTo(firstAt);

        AlertConditionState? row = await dbFactory.Context.AlertConditionStates
            .Where(s => (s.AlertRuleId == 7) && (s.MachineId == 42))
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.FirstTriggeredAt).IsEqualTo(firstAt);
        await Assert.That(row.LastObservedAt).IsEqualTo(lateNow);
    }

    [Test]
    public async Task UpsertObservationAsync_DifferentRuleMachinePairs_RemainIndependent()
    {
        // Intent: per-rule-per-machine independence. Inserting for (rule=1, machine=1) must not
        // affect (rule=1, machine=2) or (rule=2, machine=1).
        using TestDatabaseFactory dbFactory = new();
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        DateTimeOffset t1 = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset t2 = new(2026, 4, 1, 0, 5, 0, TimeSpan.Zero);
        DateTimeOffset t3 = new(2026, 4, 1, 0, 10, 0, TimeSpan.Zero);

        await repo.UpsertObservationAsync(1, 1, t1, ObservationGap, CancellationToken.None);
        await repo.UpsertObservationAsync(1, 2, t2, ObservationGap, CancellationToken.None);
        await repo.UpsertObservationAsync(2, 1, t3, ObservationGap, CancellationToken.None);

        List<AlertConditionState> all = await dbFactory.Context.AlertConditionStates.ToListAsync();
        await Assert.That(all.Count).IsEqualTo(3);

        AlertConditionState row11 = all.Single(s => (s.AlertRuleId == 1) && (s.MachineId == 1));
        AlertConditionState row12 = all.Single(s => (s.AlertRuleId == 1) && (s.MachineId == 2));
        AlertConditionState row21 = all.Single(s => (s.AlertRuleId == 2) && (s.MachineId == 1));

        await Assert.That(row11.FirstTriggeredAt).IsEqualTo(t1);
        await Assert.That(row12.FirstTriggeredAt).IsEqualTo(t2);
        await Assert.That(row21.FirstTriggeredAt).IsEqualTo(t3);
    }

    [Test]
    public async Task UpsertObservationAsync_StoredObservationOlderThanTheGap_RestartsTheWindowAtTheNewObservation()
    {
        // Intent: a host that was paused or silent stops being observed, and its condition row is left
        // behind. When it is observed again the window must start fresh: carrying the old
        // FirstTriggeredAt forward would make the first pass after the gap look like a condition that
        // had held for the whole stretch, and page immediately off data nobody was watching.
        using TestDatabaseFactory dbFactory = new();
        DateTimeOffset firstAt = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        DateTimeOffset lastAt = new(2026, 3, 1, 8, 5, 0, TimeSpan.Zero);
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = 7, MachineId = 42, FirstTriggeredAt = firstAt, LastObservedAt = lastAt,
        });
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = lastAt + ObservationGap + TimeSpan.FromSeconds(1);
        DateTimeOffset returned = await repo.UpsertObservationAsync(alertRuleId: 7, machineId: 42, now, ObservationGap, CancellationToken.None);

        await Assert.That(returned).IsEqualTo(now);
        AlertConditionState? row = await repo.GetAsync(alertRuleId: 7, machineId: 42, CancellationToken.None);
        await Assert.That(row!.FirstTriggeredAt).IsEqualTo(now);
        await Assert.That(row.LastObservedAt).IsEqualTo(now);
    }

    [Test]
    [Arguments(0)]
    [Arguments(60)]
    [Arguments(120)]
    [Arguments(180)]
    public async Task UpsertObservationAsync_StoredObservationWithinTheGap_KeepsTheOriginalFirstTriggered(int secondsSinceLastObservation)
    {
        // Intent: a pass or two lost to scheduling jitter is not a gap. The boundary is exclusive: an
        // observation exactly one gap old still counts as continuous, so only a missed stretch longer
        // than three minutes restarts the window.
        using TestDatabaseFactory dbFactory = new();
        DateTimeOffset firstAt = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        DateTimeOffset lastAt = new(2026, 3, 1, 8, 5, 0, TimeSpan.Zero);
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = 7, MachineId = 42, FirstTriggeredAt = firstAt, LastObservedAt = lastAt,
        });
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = lastAt.AddSeconds(secondsSinceLastObservation);
        DateTimeOffset returned = await repo.UpsertObservationAsync(alertRuleId: 7, machineId: 42, now, ObservationGap, CancellationToken.None);

        await Assert.That(returned).IsEqualTo(firstAt);
        AlertConditionState? row = await repo.GetAsync(alertRuleId: 7, machineId: 42, CancellationToken.None);
        await Assert.That(row!.FirstTriggeredAt).IsEqualTo(firstAt);
        await Assert.That(row.LastObservedAt).IsEqualTo(now);
    }

    [Test]
    public async Task UpsertObservationAsync_TwoConcurrentCalls_ProduceOneRowAndStableFirstTriggered()
    {
        // Intent: Two workers observing the same (rule, machine) at nearly the same time must
        // not produce a unique-constraint exception. The earlier FirstTriggeredAt must win;
        // both calls must return the same FirstTriggeredAt value. This simulates the Hangfire
        // multi-worker race where two AlertEvaluationJob ticks for the same rule/machine pair
        // can both observe "no existing row" via the read branch and both attempt an INSERT.
        // The losing INSERT must fall through to an UPDATE that returns the surviving row's
        // FirstTriggeredAt rather than bubbling a unique-constraint exception.
        using TestDatabaseFactory dbFactory = new();
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        const int ruleId = 77;
        const long machineId = 88;

        DateTimeOffset t1 = DateTimeOffset.UtcNow;
        DateTimeOffset t2 = t1.AddMilliseconds(50);

        Task<DateTimeOffset> a = repo.UpsertObservationAsync(ruleId, machineId, t1, ObservationGap, CancellationToken.None);
        Task<DateTimeOffset> b = repo.UpsertObservationAsync(ruleId, machineId, t2, ObservationGap, CancellationToken.None);
        DateTimeOffset[] results = await Task.WhenAll(a, b);

        AlertConditionState? row = await repo.GetAsync(ruleId, machineId, CancellationToken.None);
        await Assert.That(row).IsNotNull();
        // The earlier of the two timestamps must be the surviving FirstTriggeredAt — regardless
        // of which task happened to win the INSERT race, the row's FirstTriggeredAt is the t1
        // value the winning INSERT wrote. Both callers must observe that same value.
        await Assert.That(row!.FirstTriggeredAt).IsEqualTo(t1);
        await Assert.That(results[0]).IsEqualTo(t1);
        await Assert.That(results[1]).IsEqualTo(t1);

        int rowCount = await dbFactory.Context.AlertConditionStates
            .Where(s => (s.AlertRuleId == ruleId) && (s.MachineId == machineId))
            .CountAsync();
        await Assert.That(rowCount).IsEqualTo(1);
    }

    [Test]
    public async Task DeleteAsync_RowExists_RemovesIt()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = 9,
            MachineId = 3,
            FirstTriggeredAt = DateTimeOffset.UtcNow,
            LastObservedAt = DateTimeOffset.UtcNow,
        });

        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        await repo.DeleteAsync(alertRuleId: 9, machineId: 3, CancellationToken.None);

        AlertConditionState? row = await dbFactory.Context.AlertConditionStates
            .Where(s => (s.AlertRuleId == 9) && (s.MachineId == 3))
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNull();
    }

    [Test]
    public async Task DeleteAsync_RowMissing_IsNoOp()
    {
        // Intent: the job calls DeleteAsync whenever the condition clears, even on the cycles
        // when no row exists. The repo must not throw in that case.
        using TestDatabaseFactory dbFactory = new();
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        await repo.DeleteAsync(alertRuleId: 1, machineId: 1, CancellationToken.None);

        int count = await dbFactory.Context.AlertConditionStates.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task DeleteAsync_DoesNotAffectOtherRuleOrMachineRows()
    {
        // Intent: when one machine clears its condition, other machines' tracking rows must
        // survive untouched.
        using TestDatabaseFactory dbFactory = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = 10, MachineId = 1, FirstTriggeredAt = now, LastObservedAt = now,
        });
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = 10, MachineId = 2, FirstTriggeredAt = now, LastObservedAt = now,
        });
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = 11, MachineId = 1, FirstTriggeredAt = now, LastObservedAt = now,
        });

        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        await repo.DeleteAsync(alertRuleId: 10, machineId: 1, CancellationToken.None);

        List<AlertConditionState> remaining = await dbFactory.Context.AlertConditionStates.ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(2);
        await Assert.That(remaining.Any(s => (s.AlertRuleId == 10) && (s.MachineId == 1))).IsFalse();
    }

    [Test]
    public async Task DeleteForRuleAsync_RemovesAllRowsForRule_OnlyForThatRule()
    {
        // Intent: deleting a rule must wipe its tracking state across every machine. Other rules
        // must be untouched.
        using TestDatabaseFactory dbFactory = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int machineId = 1; machineId <= 3; machineId++)
        {
            await dbFactory.Context.InsertAsync(new AlertConditionState
            {
                AlertRuleId = 100, MachineId = machineId, FirstTriggeredAt = now, LastObservedAt = now,
            });
        }
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = 200, MachineId = 1, FirstTriggeredAt = now, LastObservedAt = now,
        });

        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        await repo.DeleteForRuleAsync(alertRuleId: 100, CancellationToken.None);

        List<AlertConditionState> remaining = await dbFactory.Context.AlertConditionStates.ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(1);
        await Assert.That(remaining[0].AlertRuleId).IsEqualTo(200);
    }

    [Test]
    public async Task DeleteForMachinesAfterAsync_After_DeletesOnlyThePausedMachinesOfThatTenant()
    {
        // Intent: the cutoff is exclusive (the machine whose id is the cutoff stays active) and scoped
        // to the tenant. The state rows carry no tenant of their own, so the scope comes from the
        // machine, and another tenant's higher-numbered machine must never be touched.
        using TestDatabaseFactory dbFactory = new();
        long active = await InsertMachineAsync(dbFactory, tenantId: 1);
        long pausedA = await InsertMachineAsync(dbFactory, tenantId: 1);
        long pausedB = await InsertMachineAsync(dbFactory, tenantId: 1);
        long otherTenant = await InsertMachineAsync(dbFactory, tenantId: 2);
        await InsertStateAsync(dbFactory, ruleId: 1, machineId: active);
        await InsertStateAsync(dbFactory, ruleId: 1, machineId: pausedA);
        await InsertStateAsync(dbFactory, ruleId: 2, machineId: pausedA);
        await InsertStateAsync(dbFactory, ruleId: 1, machineId: pausedB);
        await InsertStateAsync(dbFactory, ruleId: 1, machineId: otherTenant);
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        int deleted = await repo.DeleteForMachinesAfterAsync(tenantId: 1, QuotaCutoffKind.After, cutoffId: active, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(3);
        List<AlertConditionState> remaining = await dbFactory.Context.AlertConditionStates.ToListAsync();
        await Assert.That(remaining.Select(s => s.MachineId).Order().ToList()).IsEquivalentTo(new List<long> { active, otherTenant }.Order().ToList());
    }

    [Test]
    public async Task DeleteForMachinesAfterAsync_All_DeletesEveryMachineOfThatTenantOnly()
    {
        using TestDatabaseFactory dbFactory = new();
        long first = await InsertMachineAsync(dbFactory, tenantId: 1);
        long second = await InsertMachineAsync(dbFactory, tenantId: 1);
        long otherTenant = await InsertMachineAsync(dbFactory, tenantId: 2);
        await InsertStateAsync(dbFactory, ruleId: 1, machineId: first);
        await InsertStateAsync(dbFactory, ruleId: 1, machineId: second);
        await InsertStateAsync(dbFactory, ruleId: 1, machineId: otherTenant);
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        int deleted = await repo.DeleteForMachinesAfterAsync(tenantId: 1, QuotaCutoffKind.All, cutoffId: 0, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(2);
        List<AlertConditionState> remaining = await dbFactory.Context.AlertConditionStates.ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(1);
        await Assert.That(remaining[0].MachineId).IsEqualTo(otherTenant);
    }

    [Test]
    public async Task DeleteForMachinesAfterAsync_None_DeletesNothing()
    {
        // Intent: a tenant within its limit has no paused machines. None must not be read as a cutoff of
        // zero, which would delete every row the tenant has.
        using TestDatabaseFactory dbFactory = new();
        long machine = await InsertMachineAsync(dbFactory, tenantId: 1);
        await InsertStateAsync(dbFactory, ruleId: 1, machineId: machine);
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        int deleted = await repo.DeleteForMachinesAfterAsync(tenantId: 1, QuotaCutoffKind.None, cutoffId: 0, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(0);
        await Assert.That(await dbFactory.Context.AlertConditionStates.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task DeleteForMachinesAfterAsync_UnknownKind_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repo.DeleteForMachinesAfterAsync(tenantId: 1, (QuotaCutoffKind)99, cutoffId: 0, CancellationToken.None));
    }

    [Test]
    public async Task DeleteStaleAsync_OnlyDeletesRowsOlderThanThreshold()
    {
        // Intent: the reaper job removes rows whose LastObservedAt is past the retention window.
        // Recent rows must survive — they may still be tracking an actively-pending alert.
        using TestDatabaseFactory dbFactory = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = 1, MachineId = 1, FirstTriggeredAt = now.AddDays(-2), LastObservedAt = now.AddDays(-2),
        });
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = 1, MachineId = 2, FirstTriggeredAt = now.AddHours(-1), LastObservedAt = now.AddHours(-1),
        });

        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        int deleted = await repo.DeleteStaleAsync(olderThan: now.AddHours(-12), CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(1);
        List<AlertConditionState> remaining = await dbFactory.Context.AlertConditionStates.ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(1);
        await Assert.That(remaining[0].MachineId).IsEqualTo(2L);
    }

    [Test]
    public async Task DeleteStaleAsync_NoStaleRows_ReturnsZero()
    {
        // Intent: the reaper must be safe to invoke when nothing is stale — it should not throw
        // and should report zero rows deleted.
        using TestDatabaseFactory dbFactory = new();
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        int deleted = await repo.DeleteStaleAsync(olderThan: DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(0);
    }

    [Test]
    public async Task DeleteForRuleAsync_NoMatchingRows_IsNoOp()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        await repo.DeleteForRuleAsync(alertRuleId: 999, CancellationToken.None);

        int count = await dbFactory.Context.AlertConditionStates.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task AlertRuleDelete_CascadesToAlertConditionStates()
    {
        // Intent: deleting an AlertRule must cascade-delete its AlertConditionState rows.
        // Without ON DELETE CASCADE the rule-delete endpoint cannot remove a rule whose
        // duration-window tracking state exists, causing the delete to fail with an
        // FK violation in production. This asserts the live cascade behavior rather than
        // inspecting DDL text — under FKs ON, deleting the parent row must remove the child.
        using TestDatabaseFactory dbFactory = new();
        EnableForeignKeys(dbFactory);

        (int tenantId, long machineId, int ruleId) = await SeedRuleAndMachineAsync(dbFactory);

        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = ruleId,
            MachineId = machineId,
            FirstTriggeredAt = now,
            LastObservedAt = now,
        });

        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        // Sanity: the state row exists before we delete the parent.
        AlertConditionState? before = await repo.GetAsync(ruleId, machineId, CancellationToken.None);
        await Assert.That(before).IsNotNull();

        int deletedRules = await dbFactory.Context.AlertRules
            .Where(r => r.Id == ruleId)
            .DeleteAsync();
        await Assert.That(deletedRules).IsEqualTo(1);

        AlertConditionState? after = await repo.GetAsync(ruleId, machineId, CancellationToken.None);
        await Assert.That(after).IsNull();
    }

    [Test]
    public async Task MachineDelete_CascadesToAlertConditionStates()
    {
        // Intent: deleting a Machine must cascade-delete any AlertConditionState rows.
        // Without ON DELETE CASCADE, decommissioning a machine fails with an FK violation
        // whenever the machine has a duration-pending alert state row. This asserts the
        // live cascade behavior rather than inspecting DDL text.
        using TestDatabaseFactory dbFactory = new();
        EnableForeignKeys(dbFactory);

        (int tenantId, long machineId, int ruleId) = await SeedRuleAndMachineAsync(dbFactory);

        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = ruleId,
            MachineId = machineId,
            FirstTriggeredAt = now,
            LastObservedAt = now,
        });

        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        AlertConditionState? before = await repo.GetAsync(ruleId, machineId, CancellationToken.None);
        await Assert.That(before).IsNotNull();

        int deletedMachines = await dbFactory.Context.Machines
            .Where(m => m.Id == machineId)
            .DeleteAsync();
        await Assert.That(deletedMachines).IsEqualTo(1);

        AlertConditionState? after = await repo.GetAsync(ruleId, machineId, CancellationToken.None);
        await Assert.That(after).IsNull();
    }

    private static async Task<long> InsertMachineAsync(TestDatabaseFactory dbFactory, int tenantId)
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);

        return await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
    }

    private static async Task InsertStateAsync(TestDatabaseFactory dbFactory, int ruleId, long machineId)
    {
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        await dbFactory.Context.InsertAsync(new AlertConditionState
        {
            AlertRuleId = ruleId, MachineId = machineId, FirstTriggeredAt = now, LastObservedAt = now,
        });
    }

    /// <summary>
    /// Turns SQLite foreign-key enforcement on for the underlying connection so the
    /// behavioral cascade tests exercise real ON DELETE CASCADE semantics. The base
    /// <see cref="TestDatabaseFactory"/> defaults to FKs OFF so other tests can seed
    /// orphan rows without dragging in the full parent graph.
    /// </summary>
    private static void EnableForeignKeys(TestDatabaseFactory dbFactory)
    {
        SqliteConnection connection = (SqliteConnection)dbFactory.Context.OpenDbConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Seeds a User, Tenant, Machine, and AlertRule so an AlertConditionState row can be
    /// inserted under FK-enforcement and the parent records can later be deleted to
    /// exercise the cascade.
    /// </summary>
    private static async Task<(int tenantId, long machineId, int ruleId)> SeedRuleAndMachineAsync(
        TestDatabaseFactory dbFactory)
    {
        DatabaseContext db = dbFactory.Context;

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await db.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        int ruleId = await db.InsertWithInt32IdentityAsync(rule);

        return (tenantId, machineId, ruleId);
    }

    [Test]
    public async Task GetAsync_CancellationTokenPassedThrough_DoesNotThrow()
    {
        // Intent: the repository must accept a non-cancelled token without complaint. We don't
        // assert OperationCanceledException because LinqToDB/SQLite cancellation semantics for
        // short queries are timing-dependent; the contract test is "token is forwarded".
        using TestDatabaseFactory dbFactory = new();
        IAlertConditionStateRepository repo = CreateRepo(dbFactory);

        using CancellationTokenSource cts = new();

        AlertConditionState? result = await repo.GetAsync(1, 1, cts.Token);

        await Assert.That(result).IsNull();
    }
}
