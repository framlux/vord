// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services;

public sealed class AlertEvaluationJobTests
{
    // ----- Pure helpers: EvaluateCondition -----

    [Test]
    public async Task EvaluateCondition_GreaterThan_AboveThreshold_True()
    {
        await Assert.That(AlertEvaluationJob.EvaluateCondition(81m, AlertOperator.GreaterThan, 80m)).IsTrue();
    }

    [Test]
    public async Task EvaluateCondition_GreaterThan_AtThreshold_False()
    {
        await Assert.That(AlertEvaluationJob.EvaluateCondition(80m, AlertOperator.GreaterThan, 80m)).IsFalse();
    }

    [Test]
    public async Task EvaluateCondition_LessThan_Below_True()
    {
        await Assert.That(AlertEvaluationJob.EvaluateCondition(19m, AlertOperator.LessThan, 20m)).IsTrue();
    }

    [Test]
    public async Task EvaluateCondition_EqualTo_True()
    {
        await Assert.That(AlertEvaluationJob.EvaluateCondition(50m, AlertOperator.EqualTo, 50m)).IsTrue();
    }

    [Test]
    public async Task EvaluateCondition_EqualTo_False()
    {
        await Assert.That(AlertEvaluationJob.EvaluateCondition(49m, AlertOperator.EqualTo, 50m)).IsFalse();
    }

    [Test]
    public async Task EvaluateCondition_UnknownOperator_False()
    {
        await Assert.That(AlertEvaluationJob.EvaluateCondition(80m, (AlertOperator)99, 80m)).IsFalse();
    }

    [Test]
    public async Task EvaluateCondition_LessThan_AboveThreshold_ReturnsFalse()
    {
        // Intent: LessThan must be a strict comparison from below. A value above the threshold
        // must never report a breach for a LessThan rule (would generate phantom alerts for e.g.
        // "free disk space < 10%" when disk has 90% free).
        await Assert.That(AlertEvaluationJob.EvaluateCondition(21m, AlertOperator.LessThan, 20m)).IsFalse();
    }

    [Test]
    public async Task EvaluateCondition_LessThan_AtThreshold_ReturnsFalse()
    {
        // Intent: LessThan is strict, not <=. A value equal to the threshold must not trigger.
        // Catches a regression where the comparator is loosened to <= and fires one tick early.
        await Assert.That(AlertEvaluationJob.EvaluateCondition(20m, AlertOperator.LessThan, 20m)).IsFalse();
    }

    [Test]
    public async Task EvaluateCondition_GreaterThan_BelowThreshold_ReturnsFalse()
    {
        // Intent: GreaterThan must reject values strictly below the threshold. Catches a flip of
        // the comparator (>= or <) that would either over-fire or never fire.
        await Assert.That(AlertEvaluationJob.EvaluateCondition(79m, AlertOperator.GreaterThan, 80m)).IsFalse();
    }

    [Test]
    public async Task EvaluateCondition_DecimalPrecision_80_01_vs_80_00_ComparesCorrectly()
    {
        // Intent: thresholds and metric values are decimal precisely so that 80.01 > 80.00 is
        // true without floating-point rounding. Catches a switch to double which would lose this
        // boundary case under accumulation.
        await Assert.That(AlertEvaluationJob.EvaluateCondition(80.01m, AlertOperator.GreaterThan, 80.00m)).IsTrue();
    }

    [Test]
    public async Task EvaluateCondition_NegativeValue_HandlesCorrectly()
    {
        // Intent: comparison logic must work for negative values. While telemetry rarely emits
        // negative percentages, derived metrics (e.g., free space deltas) can be negative; the
        // comparator must be sign-agnostic.
        await Assert.That(AlertEvaluationJob.EvaluateCondition(-5m, AlertOperator.LessThan, 0m)).IsTrue();
    }

    [Test]
    public async Task EvaluateCondition_ZeroThreshold_HandlesCorrectly()
    {
        // Intent: a zero threshold is a valid configuration ("any non-zero value triggers");
        // the comparator must not short-circuit on threshold == 0.
        await Assert.That(AlertEvaluationJob.EvaluateCondition(1m, AlertOperator.GreaterThan, 0m)).IsTrue();
    }

    // ----- Pure helpers: GetMetricValue -----

    [Test]
    public async Task GetMetricValue_CpuUsage_ReturnsNullableDecimal()
    {
        MachineStateSummary s = new() { MachineId = 1, CpuUsagePercent = 74, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.CpuUsage, s)).IsEqualTo(74m);
    }

    [Test]
    public async Task GetMetricValue_CpuUsage_Null_ReturnsNull()
    {
        MachineStateSummary s = new() { MachineId = 1, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.CpuUsage, s)).IsNull();
    }

    [Test]
    public async Task GetMetricValue_MemoryUsage_ReturnsValue()
    {
        MachineStateSummary s = new() { MachineId = 1, MemoryUsagePercent = 55, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.MemoryUsage, s)).IsEqualTo(55m);
    }

    [Test]
    public async Task GetMetricValue_DiskUsage_DelegatesToGetMaxDiskUsage()
    {
        MachineStateSummary s = new() { MachineId = 1, MaxDiskUsagePercent = 91, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.DiskUsage, s)).IsEqualTo(91m);
    }

    [Test]
    public async Task GetMetricValue_FailedServices_ReturnsValue()
    {
        MachineStateSummary s = new() { MachineId = 1, FailedServices = 3, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.FailedServices, s)).IsEqualTo(3m);
    }

    [Test]
    public async Task GetMetricValue_FailedServices_NullValue_ReturnsNull()
    {
        // Intent: a missing FailedServices reading must produce a null metric value so the
        // evaluator short-circuits and does NOT treat "no data" as zero (would auto-resolve
        // standing alerts incorrectly).
        MachineStateSummary s = new() { MachineId = 1, FailedServices = null, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.FailedServices, s)).IsNull();
    }

    [Test]
    public async Task GetMetricValue_SecurityUpdates_ReturnsValue()
    {
        MachineStateSummary s = new() { MachineId = 1, SecurityUpdates = 12, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.SecurityUpdates, s)).IsEqualTo(12m);
    }

    [Test]
    public async Task GetMetricValue_SecurityUpdates_NullValue_ReturnsNull()
    {
        // Intent: a missing SecurityUpdates count must yield null so the evaluator can't fire
        // a security-update alert based on stale or absent data.
        MachineStateSummary s = new() { MachineId = 1, SecurityUpdates = null, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.SecurityUpdates, s)).IsNull();
    }

    [Test]
    public async Task GetMetricValue_DiskHealth_True_Returns1()
    {
        MachineStateSummary s = new() { MachineId = 1, HasDiskHealthIssue = true, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.DiskHealth, s)).IsEqualTo(1m);
    }

    [Test]
    public async Task GetMetricValue_DiskHealth_False_Returns0()
    {
        MachineStateSummary s = new() { MachineId = 1, HasDiskHealthIssue = false, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.DiskHealth, s)).IsEqualTo(0m);
    }

    [Test]
    public async Task GetMetricValue_DiskHealth_Null_ReturnsNull()
    {
        MachineStateSummary s = new() { MachineId = 1, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.DiskHealth, s)).IsNull();
    }

    [Test]
    public async Task GetMetricValue_MachineOffline_HealthStatusOffline_Returns1()
    {
        MachineStateSummary s = new() { MachineId = 1, HealthStatus = AlertConstants.HealthStatusOffline, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.MachineOffline, s)).IsEqualTo(1m);
    }

    [Test]
    public async Task GetMetricValue_MachineOffline_HealthStatusOnline_Returns0()
    {
        MachineStateSummary s = new() { MachineId = 1, HealthStatus = 0, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.MachineOffline, s)).IsEqualTo(0m);
    }

    [Test]
    public async Task GetMetricValue_SshConnection_AlwaysNull()
    {
        // SshConnection is an event-metric; should never reach GetMetricValue in normal flow.
        MachineStateSummary s = new() { MachineId = 1, CpuUsagePercent = 90, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue(AlertMetric.SshConnection, s)).IsNull();
    }

    [Test]
    public async Task GetMetricValue_UnknownMetric_ReturnsNull()
    {
        MachineStateSummary s = new() { MachineId = 1, LastSeenAt = DateTimeOffset.UtcNow };
        await Assert.That(AlertEvaluationJob.GetMetricValue((AlertMetric)99, s)).IsNull();
    }

    // ----- Integration helpers -----

    private static (AlertEvaluationJob Job, DatabaseContext Db, IAlertConditionStateRepository ConditionStates, IAlertDeliveryService Delivery, IAlertEventRepository AlertEventRepo, IAlertRuleRepository AlertRuleRepo, IMachineStateRepository MachineStateRepo) CreateJobWithDb(
        TestDatabaseFactory dbFactory,
        ISubscriptionService? subscriptionService = null)
    {
        DatabaseContext db = dbFactory.Context;

        ILogger<DatabaseRepository> repoLogger = Substitute.For<ILogger<DatabaseRepository>>();
        DatabaseRepository repository = new(db, repoLogger);

        ISubscriptionService resolvedSubscriptionService = subscriptionService ?? Substitute.For<ISubscriptionService>();
        IAlertDeliveryService deliveryService = Substitute.For<IAlertDeliveryService>();
        ILogger<AlertEvaluationJob> logger = Substitute.For<ILogger<AlertEvaluationJob>>();

        AlertEvaluationJob job = new(
            machineStateRepository: repository,
            alertRuleRepository: repository,
            alertEventRepository: repository,
            alertConditionStateRepository: repository,
            subscriptionService: resolvedSubscriptionService,
            deliveryService: deliveryService,
            logger: logger);

        return (job, db, repository, deliveryService, repository, repository, repository);
    }

    // ----- EvaluateRuleForMachineAsync paths -----

    [Test]
    public async Task EvaluateRuleForMachineAsync_ConditionNotMet_DeletesStateAndResolvesEvents()
    {
        using TestDatabaseFactory dbFactory = new();
        (AlertEvaluationJob job, DatabaseContext db, IAlertConditionStateRepository conditionStates, _, _, _, _) = CreateJobWithDb(dbFactory);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 5);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Seed a prior condition-state row and a triggered event so we can verify both are cleared.
        await db.InsertAsync(new AlertConditionState
        {
            AlertRuleId = rule.Id, MachineId = machine.Id,
            FirstTriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        AlertEvent triggered = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id, status: AlertEventStatus.Triggered);
        triggered.Id = await db.InsertWithInt64IdentityAsync(triggered);

        // CPU now below threshold — condition not met.
        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 50, LastSeenAt = DateTimeOffset.UtcNow };

        await job.EvaluateRuleForMachineAsync(rule, state, CancellationToken.None);

        AlertConditionState? stateRow = await db.AlertConditionStates
            .Where(s => (s.AlertRuleId == rule.Id) && (s.MachineId == machine.Id))
            .FirstOrDefaultAsync();
        await Assert.That(stateRow).IsNull();

        AlertEvent? resolved = await db.AlertEvents.FirstOrDefaultAsync(e => e.Id == triggered.Id);
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Status).IsEqualTo(AlertEventStatus.Resolved);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_MetricNull_ShortCircuits_NoStateOrEventChanges()
    {
        using TestDatabaseFactory dbFactory = new();
        (AlertEvaluationJob job, DatabaseContext db, _, IAlertDeliveryService delivery, _, _, _) = CreateJobWithDb(dbFactory);

        // Unknown metric returns null from GetMetricValue.
        AlertRule rule = TestDataBuilder.BuildAlertRule(metric: (AlertMetric)99, threshold: 1m);
        rule.Id = 1;

        MachineStateSummary state = new() { MachineId = 1, LastSeenAt = DateTimeOffset.UtcNow };

        await job.EvaluateRuleForMachineAsync(rule, state, CancellationToken.None);

        int events = await db.AlertEvents.CountAsync();
        int conditionRows = await db.AlertConditionStates.CountAsync();
        await Assert.That(events).IsEqualTo(0);
        await Assert.That(conditionRows).IsEqualTo(0);
        await delivery.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_ConditionMet_NoDuration_CreatesEventAndEnqueuesDelivery()
    {
        using TestDatabaseFactory dbFactory = new();
        (AlertEvaluationJob job, DatabaseContext db, _, IAlertDeliveryService delivery, _, _, _) = CreateJobWithDb(dbFactory);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 95, LastSeenAt = DateTimeOffset.UtcNow };

        await job.EvaluateRuleForMachineAsync(rule, state, CancellationToken.None);

        List<AlertEvent> events = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).ToListAsync();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Status).IsEqualTo(AlertEventStatus.Triggered);
        await delivery.Received(1).EnqueueAsync(events[0].Id, rule.Id, rule.TenantId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_ConditionMet_NoDuration_ActiveEventExists_NoDelivery()
    {
        // Intent: if CreateEventIfNotExistsAsync returns null (an active event already exists), the
        // job must skip delivery enqueue to avoid double-firing.
        using TestDatabaseFactory dbFactory = new();
        (AlertEvaluationJob job, DatabaseContext db, _, IAlertDeliveryService delivery, _, _, _) = CreateJobWithDb(dbFactory);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        AlertEvent existing = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id, status: AlertEventStatus.Triggered);
        existing.Id = await db.InsertWithInt64IdentityAsync(existing);

        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 95, LastSeenAt = DateTimeOffset.UtcNow };

        await job.EvaluateRuleForMachineAsync(rule, state, CancellationToken.None);

        int totalEvents = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).CountAsync();
        await Assert.That(totalEvents).IsEqualTo(1);
        await delivery.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_ConditionMet_WithDuration_FirstObservation_InsertsStateNoEvent()
    {
        using TestDatabaseFactory dbFactory = new();
        (AlertEvaluationJob job, DatabaseContext db, _, _, _, _, _) = CreateJobWithDb(dbFactory);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 5);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 90, LastSeenAt = DateTimeOffset.UtcNow };

        await job.EvaluateRuleForMachineAsync(rule, state, CancellationToken.None);

        AlertConditionState? row = await db.AlertConditionStates
            .Where(s => (s.AlertRuleId == rule.Id) && (s.MachineId == 1))
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();

        int eventCount = await db.AlertEvents.CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_ConditionMet_WithDuration_NotYetElapsed_NoEvent_PreservesFirstTriggered()
    {
        // Intent: the duration window must measure from the original FirstTriggeredAt; subsequent
        // observations within the window must NOT reset it.
        using TestDatabaseFactory dbFactory = new();
        (AlertEvaluationJob job, DatabaseContext db, _, _, _, _, _) = CreateJobWithDb(dbFactory);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 5);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        DateTimeOffset twoMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-2);
        await db.InsertAsync(new AlertConditionState
        {
            AlertRuleId = rule.Id, MachineId = 1,
            FirstTriggeredAt = twoMinutesAgo,
            LastObservedAt = twoMinutesAgo,
        });

        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 90, LastSeenAt = DateTimeOffset.UtcNow };

        await job.EvaluateRuleForMachineAsync(rule, state, CancellationToken.None);

        AlertConditionState? row = await db.AlertConditionStates
            .Where(s => (s.AlertRuleId == rule.Id) && (s.MachineId == 1))
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();
        // FirstTriggeredAt must NOT have been reset; LastObservedAt should advance.
        await Assert.That(row!.FirstTriggeredAt).IsEqualTo(twoMinutesAgo);
        await Assert.That(row.LastObservedAt > twoMinutesAgo).IsTrue();

        int eventCount = await db.AlertEvents.CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_ConditionMet_WithDuration_Elapsed_FiresAlertAndClearsStateRow()
    {
        using TestDatabaseFactory dbFactory = new();
        (AlertEvaluationJob job, DatabaseContext db, _, IAlertDeliveryService delivery, _, _, _) = CreateJobWithDb(dbFactory);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 5);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        DateTimeOffset tenMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.InsertAsync(new AlertConditionState
        {
            AlertRuleId = rule.Id, MachineId = machine.Id,
            FirstTriggeredAt = tenMinutesAgo,
            LastObservedAt = tenMinutesAgo,
        });

        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 95, LastSeenAt = DateTimeOffset.UtcNow };

        await job.EvaluateRuleForMachineAsync(rule, state, CancellationToken.None);

        // Alert event created.
        List<AlertEvent> events = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).ToListAsync();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Status).IsEqualTo(AlertEventStatus.Triggered);

        // State row deleted so a re-trigger gets a fresh window.
        AlertConditionState? row = await db.AlertConditionStates
            .Where(s => (s.AlertRuleId == rule.Id) && (s.MachineId == machine.Id))
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNull();

        await delivery.Received(1).EnqueueAsync(events[0].Id, rule.Id, rule.TenantId, Arg.Any<CancellationToken>());
    }

    // ----- RunAsync paths -----

    [Test]
    public async Task RunAsync_NoEnabledRules_ReturnsEarly()
    {
        using TestDatabaseFactory dbFactory = new();
        (AlertEvaluationJob job, _, _, IAlertDeliveryService delivery, _, _, _) = CreateJobWithDb(dbFactory);

        await job.RunAsync(CancellationToken.None);

        await delivery.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_FreeTier_SkipsTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        (AlertEvaluationJob job, DatabaseContext db, _, _, _, _, _) = CreateJobWithDb(dbFactory, subscriptionService);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription freeSub = TestDataBuilder.BuildSubscription(tenantId: tenant.Id, tier: SubscriptionTier.Free, status: SubscriptionStatus.Active);
        await db.InsertWithInt32IdentityAsync(freeSub);
        subscriptionService.GetSubscriptionForTenantAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns(freeSub);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, cpuPercent: 95));

        await job.RunAsync(CancellationToken.None);

        int events = await db.AlertEvents.CountAsync();
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_NullSubscription_SkipsTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        (AlertEvaluationJob job, DatabaseContext db, _, _, _, _, _) = CreateJobWithDb(dbFactory, subscriptionService);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);
        subscriptionService.GetSubscriptionForTenantAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns((TenantSubscription?)null);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        await job.RunAsync(CancellationToken.None);

        int events = await db.AlertEvents.CountAsync();
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_InactiveSubscription_SkipsTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        (AlertEvaluationJob job, DatabaseContext db, _, _, _, _, _) = CreateJobWithDb(dbFactory, subscriptionService);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription canceled = TestDataBuilder.BuildSubscription(tenantId: tenant.Id, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Canceled);
        subscriptionService.GetSubscriptionForTenantAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns(canceled);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        await job.RunAsync(CancellationToken.None);

        int events = await db.AlertEvents.CountAsync();
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_ProTier_BreachingRule_AssignedToMachine_FiresAlertAndEnqueuesDelivery()
    {
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        (AlertEvaluationJob job, DatabaseContext db, _, IAlertDeliveryService delivery, _, _, _) = CreateJobWithDb(dbFactory, subscriptionService);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription proSub = TestDataBuilder.BuildSubscription(tenantId: tenant.Id, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        await db.InsertWithInt32IdentityAsync(proSub);
        subscriptionService.GetSubscriptionForTenantAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns(proSub);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        await db.InsertAsync(new AlertRuleMachine { AlertRuleId = rule.Id, MachineId = machine.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, tenantId: tenant.Id, cpuPercent: 95));

        await job.RunAsync(CancellationToken.None);

        int events = await db.AlertEvents.CountAsync();
        await Assert.That(events).IsEqualTo(1);
        await delivery.Received(1).EnqueueAsync(Arg.Any<long>(), rule.Id, tenant.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_RuleHasNoAssignedMachines_Skipped()
    {
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        (AlertEvaluationJob job, DatabaseContext db, _, _, _, _, _) = CreateJobWithDb(dbFactory, subscriptionService);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription proSub = TestDataBuilder.BuildSubscription(tenantId: tenant.Id, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        subscriptionService.GetSubscriptionForTenantAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns(proSub);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);
        // No AlertRuleMachine rows — rule has no assigned machines.

        await job.RunAsync(CancellationToken.None);

        int events = await db.AlertEvents.CountAsync();
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_SshConnectionEventMetric_SkippedInPeriodicEvaluation()
    {
        // Intent: event-based metrics (currently only SshConnection) must NEVER be evaluated by
        // the periodic loop; they are emitted at telemetry-ingest. The IsEventMetric switch
        // short-circuits these rules.
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        (AlertEvaluationJob job, DatabaseContext db, _, _, _, _, _) = CreateJobWithDb(dbFactory, subscriptionService);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);
        TenantSubscription proSub = TestDataBuilder.BuildSubscription(tenantId: tenant.Id, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        subscriptionService.GetSubscriptionForTenantAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns(proSub);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule sshRule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.SshConnection, threshold: 1m, durationMinutes: 0);
        sshRule.Id = await db.InsertWithInt32IdentityAsync(sshRule);

        await db.InsertAsync(new AlertRuleMachine { AlertRuleId = sshRule.Id, MachineId = machine.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, tenantId: tenant.Id, cpuPercent: 95));

        await job.RunAsync(CancellationToken.None);

        int events = await db.AlertEvents.CountAsync();
        await Assert.That(events).IsEqualTo(0);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_DurationRule_ComesBackOnline_ClearsState()
    {
        // Intent: when a machine that previously breached an offline-with-duration rule comes
        // back online before the duration elapses, the in-flight AlertConditionState row must
        // be cleared so that the next outage starts a fresh window from zero. Without this the
        // machine could flap online/offline and accumulate "time offline" across distinct outages.
        using TestDatabaseFactory dbFactory = new();
        (AlertEvaluationJob job, DatabaseContext db, _, IAlertDeliveryService delivery, _, _, _) = CreateJobWithDb(dbFactory);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.MachineOffline, op: AlertOperator.GreaterThan, threshold: 0m, durationMinutes: 5);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Seed an in-flight condition-state row from when the machine first went offline.
        DateTimeOffset twoMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-2);
        await db.InsertAsync(new AlertConditionState
        {
            AlertRuleId = rule.Id, MachineId = machine.Id,
            FirstTriggeredAt = twoMinutesAgo,
            LastObservedAt = twoMinutesAgo,
        });

        // Machine is now back online (HealthStatus = 0 → metric value 0, fails threshold > 0).
        MachineStateSummary state = new() { MachineId = machine.Id, HealthStatus = 0, LastSeenAt = DateTimeOffset.UtcNow };

        await job.EvaluateRuleForMachineAsync(rule, state, CancellationToken.None);

        // Condition-state row must be cleared.
        AlertConditionState? row = await db.AlertConditionStates
            .Where(s => (s.AlertRuleId == rule.Id) && (s.MachineId == machine.Id))
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNull();

        // No alert event was created, and no delivery was enqueued.
        int events = await db.AlertEvents.CountAsync();
        await Assert.That(events).IsEqualTo(0);
        await delivery.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_AcknowledgedEvent_BlocksNewAlert()
    {
        // Intent: HIGH-VALUE dedup invariant verified at the JOB layer (the repository-layer
        // contract is covered by AlertEventRepositoryTests.CreateEventIfNotExistsAsync_AcknowledgedEvent_BlocksNewDuplicateAlert).
        // If the job ever stopped honoring CreateEventIfNotExistsAsync's "active event already
        // exists" return value, an acknowledged-but-unresolved alert would re-page the on-call
        // every minute while the condition persists. This test exercises the full job pipeline:
        // a met condition + an existing Acknowledged event => no new row, no delivery.
        using TestDatabaseFactory dbFactory = new();
        (AlertEvaluationJob job, DatabaseContext db, _, IAlertDeliveryService delivery, _, _, _) = CreateJobWithDb(dbFactory);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Seed an Acknowledged event (the operator has seen the alert but the condition has not
        // cleared yet).
        AlertEvent acknowledged = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id, status: AlertEventStatus.Acknowledged);
        acknowledged.AcknowledgedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        acknowledged.Id = await db.InsertWithInt64IdentityAsync(acknowledged);

        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 95, LastSeenAt = DateTimeOffset.UtcNow };

        await job.EvaluateRuleForMachineAsync(rule, state, CancellationToken.None);

        // Still exactly one event — the acknowledged one. No new row was inserted.
        List<AlertEvent> events = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).ToListAsync();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Id).IsEqualTo(acknowledged.Id);
        await Assert.That(events[0].Status).IsEqualTo(AlertEventStatus.Acknowledged);

        // And no delivery was enqueued.
        await delivery.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_DisabledRules_NotEvaluated()
    {
        // Intent: disabled rules (IsEnabled = false) must be excluded from the evaluation set.
        // The repository's GetEnabledAlertRulesAsync filters them out; if a future change loaded
        // ALL rules and filtered later, a disabled rule could accidentally fire. With ONLY
        // disabled rules seeded, RunAsync must short-circuit to the no-rules path and never
        // enqueue any delivery.
        using TestDatabaseFactory dbFactory = new();
        (AlertEvaluationJob job, DatabaseContext db, _, IAlertDeliveryService delivery, _, _, _) = CreateJobWithDb(dbFactory);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule disabled = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, isEnabled: false);
        disabled.Id = await db.InsertWithInt32IdentityAsync(disabled);

        // A machine state that WOULD breach the rule if it were evaluated.
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, tenantId: tenant.Id, cpuPercent: 95));

        await job.RunAsync(CancellationToken.None);

        int events = await db.AlertEvents.CountAsync();
        await Assert.That(events).IsEqualTo(0);
        await delivery.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_MultipleTenantsMultipleRules_EvaluatesCorrectly()
    {
        // Intent: per-tenant grouping and rule-scoped machine assignment must produce exactly
        // one event per (tenant, rule, breaching machine). This catches:
        //   - cross-tenant leakage (firing tenant A's rule against tenant B's machines)
        //   - rule-machine mis-association (firing rule R1 for a machine assigned only to R2)
        //   - per-tenant subscription gate not being applied uniformly across tenants
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        (AlertEvaluationJob job, DatabaseContext db, _, IAlertDeliveryService delivery, _, _, _) = CreateJobWithDb(dbFactory, subscriptionService);

        // Tenant 1: Pro tier, one CPU rule, one machine breaching CPU.
        Tenant tenant1 = TestDataBuilder.BuildTenant();
        tenant1.Id = await db.InsertWithInt32IdentityAsync(tenant1);
        TenantSubscription sub1 = TestDataBuilder.BuildSubscription(tenantId: tenant1.Id, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        subscriptionService.GetSubscriptionForTenantAsync(tenant1.Id, Arg.Any<CancellationToken>()).Returns(sub1);

        // Tenant 2: Team tier, one Memory rule, one machine breaching memory.
        Tenant tenant2 = TestDataBuilder.BuildTenant();
        tenant2.Id = await db.InsertWithInt32IdentityAsync(tenant2);
        TenantSubscription sub2 = TestDataBuilder.BuildSubscription(tenantId: tenant2.Id, tier: SubscriptionTier.Team, status: SubscriptionStatus.Active);
        subscriptionService.GetSubscriptionForTenantAsync(tenant2.Id, Arg.Any<CancellationToken>()).Returns(sub2);

        AlertRule rule1 = TestDataBuilder.BuildAlertRule(tenantId: tenant1.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule1.Id = await db.InsertWithInt32IdentityAsync(rule1);
        AlertRule rule2 = TestDataBuilder.BuildAlertRule(tenantId: tenant2.Id, metric: AlertMetric.MemoryUsage, threshold: 50m, durationMinutes: 0);
        rule2.Id = await db.InsertWithInt32IdentityAsync(rule2);

        Machine machine1 = TestDataBuilder.BuildMachine(tenantId: tenant1.Id);
        machine1.Id = await db.InsertWithInt64IdentityAsync(machine1);
        Machine machine2 = TestDataBuilder.BuildMachine(tenantId: tenant2.Id);
        machine2.Id = await db.InsertWithInt64IdentityAsync(machine2);

        // Assign each machine only to its tenant's rule.
        await db.InsertAsync(new AlertRuleMachine { AlertRuleId = rule1.Id, MachineId = machine1.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.InsertAsync(new AlertRuleMachine { AlertRuleId = rule2.Id, MachineId = machine2.Id, CreatedAt = DateTimeOffset.UtcNow });

        // Tenant 1's machine breaches CPU; tenant 2's machine breaches memory.
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machine1.Id, tenantId: tenant1.Id, cpuPercent: 95));
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machine2.Id, tenantId: tenant2.Id, memoryPercent: 75));

        await job.RunAsync(CancellationToken.None);

        // Exactly two events — one per tenant. No cross-tenant firing.
        List<AlertEvent> allEvents = await db.AlertEvents.ToListAsync();
        await Assert.That(allEvents.Count).IsEqualTo(2);

        AlertEvent? tenant1Event = allEvents.FirstOrDefault(e => e.TenantId == tenant1.Id);
        AlertEvent? tenant2Event = allEvents.FirstOrDefault(e => e.TenantId == tenant2.Id);
        await Assert.That(tenant1Event).IsNotNull();
        await Assert.That(tenant1Event!.AlertRuleId).IsEqualTo(rule1.Id);
        await Assert.That(tenant1Event.MachineId).IsEqualTo(machine1.Id);
        await Assert.That(tenant2Event).IsNotNull();
        await Assert.That(tenant2Event!.AlertRuleId).IsEqualTo(rule2.Id);
        await Assert.That(tenant2Event.MachineId).IsEqualTo(machine2.Id);

        // Delivery was enqueued once per tenant, with the correct (ruleId, tenantId) pair.
        await delivery.Received(1).EnqueueAsync(tenant1Event.Id, rule1.Id, tenant1.Id, Arg.Any<CancellationToken>());
        await delivery.Received(1).EnqueueAsync(tenant2Event.Id, rule2.Id, tenant2.Id, Arg.Any<CancellationToken>());
    }

    // ----- Constructor null guards -----

    private static AlertEvaluationJob BuildJob(
        IMachineStateRepository? machineState = null,
        IAlertRuleRepository? alertRules = null,
        IAlertEventRepository? alertEvents = null,
        IAlertConditionStateRepository? conditionStates = null,
        ISubscriptionService? subscriptions = null,
        IAlertDeliveryService? delivery = null,
        ILogger<AlertEvaluationJob>? logger = null)
    {
        return new AlertEvaluationJob(
            machineState!,
            alertRules!,
            alertEvents!,
            conditionStates!,
            subscriptions!,
            delivery!,
            logger!);
    }

    private static AlertEvaluationJob BuildJobWithAllExcept(string paramName)
    {
        IMachineStateRepository ms = Substitute.For<IMachineStateRepository>();
        IAlertRuleRepository ar = Substitute.For<IAlertRuleRepository>();
        IAlertEventRepository ae = Substitute.For<IAlertEventRepository>();
        IAlertConditionStateRepository cs = Substitute.For<IAlertConditionStateRepository>();
        ISubscriptionService ss = Substitute.For<ISubscriptionService>();
        IAlertDeliveryService ds = Substitute.For<IAlertDeliveryService>();
        ILogger<AlertEvaluationJob> log = Substitute.For<ILogger<AlertEvaluationJob>>();

        return BuildJob(
            paramName == "machineStateRepository" ? null : ms,
            paramName == "alertRuleRepository" ? null : ar,
            paramName == "alertEventRepository" ? null : ae,
            paramName == "alertConditionStateRepository" ? null : cs,
            paramName == "subscriptionService" ? null : ss,
            paramName == "deliveryService" ? null : ds,
            paramName == "logger" ? null : log);
    }

    [Test]
    public async Task Constructor_NullMachineStateRepository_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            BuildJobWithAllExcept("machineStateRepository");

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("machineStateRepository");
    }

    [Test]
    public async Task Constructor_NullAlertRuleRepository_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            BuildJobWithAllExcept("alertRuleRepository");

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("alertRuleRepository");
    }

    [Test]
    public async Task Constructor_NullAlertEventRepository_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            BuildJobWithAllExcept("alertEventRepository");

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("alertEventRepository");
    }

    [Test]
    public async Task Constructor_NullAlertConditionStateRepository_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            BuildJobWithAllExcept("alertConditionStateRepository");

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("alertConditionStateRepository");
    }

    [Test]
    public async Task Constructor_NullSubscriptionService_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            BuildJobWithAllExcept("subscriptionService");

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("subscriptionService");
    }

    [Test]
    public async Task Constructor_NullDeliveryService_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            BuildJobWithAllExcept("deliveryService");

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("deliveryService");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            BuildJobWithAllExcept("logger");

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    [Test]
    public async Task RunAsync_OneTenantSubscriptionLookupThrows_OtherTenantsStillEvaluated()
    {
        // Intent: per-tenant fault isolation. If tenant A's subscription check throws (transient
        // billing-API blip), tenant B's evaluation must still run on the same cycle. Without
        // this isolation, one bad tenant takes down the entire fleet's alert evaluation.
        IAlertRuleRepository ruleRepo = Substitute.For<IAlertRuleRepository>();
        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        IMachineStateRepository stateRepo = Substitute.For<IMachineStateRepository>();
        IAlertConditionStateRepository conditionRepo = Substitute.For<IAlertConditionStateRepository>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        IAlertDeliveryService delivery = Substitute.For<IAlertDeliveryService>();
        ILogger<AlertEvaluationJob> logger = Substitute.For<ILogger<AlertEvaluationJob>>();

        // Two tenants, each with one rule.
        AlertRule ruleA = new()
        {
            Id = 1, TenantId = 1, Name = "A",
            Metric = AlertMetric.CpuUsage, Operator = AlertOperator.GreaterThan, Threshold = 80,
            Severity = AlertSeverity.Warning, IsEnabled = true, IsCustom = true,
            CreatedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        AlertRule ruleB = new()
        {
            Id = 2, TenantId = 2, Name = "B",
            Metric = AlertMetric.CpuUsage, Operator = AlertOperator.GreaterThan, Threshold = 80,
            Severity = AlertSeverity.Warning, IsEnabled = true, IsCustom = true,
            CreatedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        ruleRepo.GetEnabledAlertRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AlertRule> { ruleA, ruleB });

        // Tenant 1 subscription lookup throws; tenant 2 returns a Pro subscription.
        subscriptionService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns<Task<TenantSubscription?>>(_ => throw new InvalidOperationException("billing API down"));
        subscriptionService.GetSubscriptionForTenantAsync(2, Arg.Any<CancellationToken>())
            .Returns(new TenantSubscription
            {
                TenantId = 2, Tier = SubscriptionTier.Pro, Status = SubscriptionStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

        // Tenant 2 has no machines so we return empty.
        ruleRepo.GetMachineIdsForRulesAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, List<long>>());
        stateRepo.GetSummariesForTenantMachinesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new List<MachineStateSummary>());

        AlertEvaluationJob job = new(stateRepo, ruleRepo, eventRepo, conditionRepo, subscriptionService, delivery, logger);

        // Must NOT throw — tenant 1 failure is caught + logged.
        await job.RunAsync(CancellationToken.None);

        // Tenant 2 was evaluated even though tenant 1 threw.
        await subscriptionService.Received(1).GetSubscriptionForTenantAsync(2, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_CancellationDuringTenantLoop_PropagatesAndStops()
    {
        // Intent: shutdown cancellation must surface, not be swallowed by the per-tenant catch.
        // The recurring schedule re-runs after a shutdown so no work is lost.
        using CancellationTokenSource cts = new();

        IAlertRuleRepository ruleRepo = Substitute.For<IAlertRuleRepository>();
        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        IMachineStateRepository stateRepo = Substitute.For<IMachineStateRepository>();
        IAlertConditionStateRepository conditionRepo = Substitute.For<IAlertConditionStateRepository>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        IAlertDeliveryService delivery = Substitute.For<IAlertDeliveryService>();
        ILogger<AlertEvaluationJob> logger = Substitute.For<ILogger<AlertEvaluationJob>>();

        AlertRule rule = new()
        {
            Id = 1, TenantId = 1, Name = "R",
            Metric = AlertMetric.CpuUsage, Operator = AlertOperator.GreaterThan, Threshold = 80,
            Severity = AlertSeverity.Warning, IsEnabled = true, IsCustom = true,
            CreatedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        ruleRepo.GetEnabledAlertRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AlertRule> { rule });

        subscriptionService.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<TenantSubscription?>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        AlertEvaluationJob job = new(stateRepo, ruleRepo, eventRepo, conditionRepo, subscriptionService, delivery, logger);

        await Assert.ThrowsAsync<OperationCanceledException>(() => job.RunAsync(cts.Token));
    }

    [Test]
    public async Task RunAsync_AutomaticRetryAttribute_IsZeroAttempts()
    {
        // Intent: pin Hangfire AutomaticRetry to 0 attempts. The default of 10 would cause
        // duplicate executions on transient failure; this job is not idempotent under retry.
        MethodInfo method = typeof(AlertEvaluationJob).GetMethod(nameof(AlertEvaluationJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        AutomaticRetryAttribute? attr = method.GetCustomAttribute<AutomaticRetryAttribute>();

        await Assert.That(attr).IsNotNull();
        await Assert.That(attr!.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_DisableConcurrentExecution_TimeoutMatchesContract()
    {
        // Intent: pin the lock timeout. Use CustomAttributeData since DisableConcurrentExecutionAttribute
        // does not expose timeout via a public property.
        MethodInfo method = typeof(AlertEvaluationJob).GetMethod(nameof(AlertEvaluationJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        CustomAttributeData? attrData = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(DisableConcurrentExecutionAttribute));

        await Assert.That(attrData).IsNotNull();
        await Assert.That(attrData!.ConstructorArguments.Count).IsEqualTo(1);
        int timeoutSeconds = (int)attrData.ConstructorArguments[0].Value!;
        await Assert.That(timeoutSeconds).IsEqualTo(600);
    }
}
