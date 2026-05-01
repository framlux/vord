// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Alerts;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="AlertEvaluationService"/> internal logic.
/// </summary>
public sealed class AlertEvaluationServiceTests
{
    // --- EvaluateCondition Tests ---

    [Test]
    public async Task EvaluateCondition_GreaterThan_AboveThreshold_ReturnsTrue()
    {
        bool result = AlertEvaluationService.EvaluateCondition(81m, AlertOperator.GreaterThan, 80m);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task EvaluateCondition_GreaterThan_AtThreshold_ReturnsFalse()
    {
        bool result = AlertEvaluationService.EvaluateCondition(80m, AlertOperator.GreaterThan, 80m);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task EvaluateCondition_GreaterThan_BelowThreshold_ReturnsFalse()
    {
        bool result = AlertEvaluationService.EvaluateCondition(79m, AlertOperator.GreaterThan, 80m);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task EvaluateCondition_LessThan_BelowThreshold_ReturnsTrue()
    {
        bool result = AlertEvaluationService.EvaluateCondition(19m, AlertOperator.LessThan, 20m);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task EvaluateCondition_LessThan_AtThreshold_ReturnsFalse()
    {
        bool result = AlertEvaluationService.EvaluateCondition(20m, AlertOperator.LessThan, 20m);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task EvaluateCondition_LessThan_AboveThreshold_ReturnsFalse()
    {
        bool result = AlertEvaluationService.EvaluateCondition(21m, AlertOperator.LessThan, 20m);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task EvaluateCondition_Equals_MatchesThreshold_ReturnsTrue()
    {
        bool result = AlertEvaluationService.EvaluateCondition(50m, AlertOperator.EqualTo, 50m);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task EvaluateCondition_Equals_DoesNotMatch_ReturnsFalse()
    {
        bool result = AlertEvaluationService.EvaluateCondition(49m, AlertOperator.EqualTo, 50m);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task EvaluateCondition_UnknownOperator_ReturnsFalse()
    {
        bool result = AlertEvaluationService.EvaluateCondition(80m, (AlertOperator)99, 80m);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task EvaluateCondition_ZeroThreshold_HandlesCorrectly()
    {
        bool result = AlertEvaluationService.EvaluateCondition(1m, AlertOperator.GreaterThan, 0m);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task EvaluateCondition_NegativeValues_HandlesCorrectly()
    {
        bool result = AlertEvaluationService.EvaluateCondition(-5m, AlertOperator.LessThan, 0m);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task EvaluateCondition_DecimalPrecision_ComparesCorrectly()
    {
        bool result = AlertEvaluationService.EvaluateCondition(80.01m, AlertOperator.GreaterThan, 80.00m);

        await Assert.That(result).IsTrue();
    }

    // --- GetMetricValue Tests ---

    [Test]
    public async Task GetMetricValue_CpuUsage_ReturnsCpuPercent()
    {
        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 75, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.CpuUsage, state);

        await Assert.That(result).IsEqualTo(75m);
    }

    [Test]
    public async Task GetMetricValue_CpuUsage_NullValue_ReturnsNull()
    {
        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = null, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.CpuUsage, state);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMetricValue_MemoryUsage_ReturnsMemoryPercent()
    {
        MachineStateSummary state = new() { MachineId = 1, MemoryUsagePercent = 60, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.MemoryUsage, state);

        await Assert.That(result).IsEqualTo(60m);
    }

    [Test]
    public async Task GetMetricValue_MemoryUsage_NullValue_ReturnsNull()
    {
        MachineStateSummary state = new() { MachineId = 1, MemoryUsagePercent = null, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.MemoryUsage, state);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMetricValue_FailedServices_ReturnsCount()
    {
        MachineStateSummary state = new() { MachineId = 1, FailedServices = 3, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.FailedServices, state);

        await Assert.That(result).IsEqualTo(3m);
    }

    [Test]
    public async Task GetMetricValue_FailedServices_NullValue_ReturnsNull()
    {
        MachineStateSummary state = new() { MachineId = 1, FailedServices = null, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.FailedServices, state);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMetricValue_SecurityUpdates_ReturnsCount()
    {
        MachineStateSummary state = new() { MachineId = 1, SecurityUpdates = 5, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.SecurityUpdates, state);

        await Assert.That(result).IsEqualTo(5m);
    }

    [Test]
    public async Task GetMetricValue_SecurityUpdates_NullValue_ReturnsNull()
    {
        MachineStateSummary state = new() { MachineId = 1, SecurityUpdates = null, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.SecurityUpdates, state);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMetricValue_MachineOffline_HealthStatus3_Returns1()
    {
        MachineStateSummary state = new() { MachineId = 1, HealthStatus = 3, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.MachineOffline, state);

        await Assert.That(result).IsEqualTo(1m);
    }

    [Test]
    public async Task GetMetricValue_MachineOffline_HealthStatus0_Returns0()
    {
        MachineStateSummary state = new() { MachineId = 1, HealthStatus = 0, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.MachineOffline, state);

        await Assert.That(result).IsEqualTo(0m);
    }

    [Test]
    public async Task GetMetricValue_MachineOffline_HealthStatus1_Returns0()
    {
        MachineStateSummary state = new() { MachineId = 1, HealthStatus = 1, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.MachineOffline, state);

        await Assert.That(result).IsEqualTo(0m);
    }

    [Test]
    public async Task GetMetricValue_MachineOffline_HealthStatus2_Returns0()
    {
        MachineStateSummary state = new() { MachineId = 1, HealthStatus = 2, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.MachineOffline, state);

        await Assert.That(result).IsEqualTo(0m);
    }

    [Test]
    public async Task GetMetricValue_UnknownMetric_ReturnsNull()
    {
        MachineStateSummary state = new() { MachineId = 1, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue((AlertMetric)99, state);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMetricValue_DiskUsage_ReturnsMaxDiskUsage()
    {
        MachineStateSummary state = new() { MachineId = 1, MaxDiskUsagePercent = 85, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.DiskUsage, state);

        await Assert.That(result).IsEqualTo(85m);
    }

    [Test]
    public async Task GetMetricValue_DiskHealth_ReturnsDiskHealthValue()
    {
        MachineStateSummary state = new() { MachineId = 1, HasDiskHealthIssue = true, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMetricValue(AlertMetric.DiskHealth, state);

        await Assert.That(result).IsEqualTo(1m);
    }

    // --- GetMaxDiskUsage Tests ---

    [Test]
    public async Task GetMaxDiskUsage_NullMaxDiskUsagePercent_ReturnsNull()
    {
        MachineStateSummary state = new() { MachineId = 1, MaxDiskUsagePercent = null, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetMaxDiskUsage(state);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMaxDiskUsage_HasValue_ReturnsDecimal()
    {
        MachineStateSummary state = new()
        {
            MachineId = 1,
            MaxDiskUsagePercent = 85,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        decimal? result = AlertEvaluationService.GetMaxDiskUsage(state);

        await Assert.That(result).IsEqualTo(85m);
    }

    [Test]
    public async Task GetMaxDiskUsage_ZeroValue_ReturnsZero()
    {
        MachineStateSummary state = new()
        {
            MachineId = 1,
            MaxDiskUsagePercent = 0,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        decimal? result = AlertEvaluationService.GetMaxDiskUsage(state);

        await Assert.That(result).IsEqualTo(0m);
    }

    // --- GetDiskHealthValue Tests ---

    [Test]
    public async Task GetDiskHealthValue_NullHasDiskHealthIssue_ReturnsNull()
    {
        MachineStateSummary state = new() { MachineId = 1, HasDiskHealthIssue = null, LastSeenAt = DateTimeOffset.UtcNow };

        decimal? result = AlertEvaluationService.GetDiskHealthValue(state);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetDiskHealthValue_NoDiskHealthIssue_ReturnsZero()
    {
        MachineStateSummary state = new()
        {
            MachineId = 1,
            HasDiskHealthIssue = false,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        decimal? result = AlertEvaluationService.GetDiskHealthValue(state);

        await Assert.That(result).IsEqualTo(0m);
    }

    [Test]
    public async Task GetDiskHealthValue_HasDiskHealthIssue_ReturnsOne()
    {
        MachineStateSummary state = new()
        {
            MachineId = 1,
            HasDiskHealthIssue = true,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        decimal? result = AlertEvaluationService.GetDiskHealthValue(state);

        await Assert.That(result).IsEqualTo(1m);
    }

    // --- EvaluateRuleForMachineAsync Tests ---

    private static (AlertEvaluationService Service, DatabaseContext Db, IDatabase RedisDb, IAlertDeliveryService Delivery, IAlertEventRepository AlertEventRepo) CreateServiceWithDb(
        ISubscriptionService? subscriptionService = null)
    {
        TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        ILogger<DatabaseRepository> repoLogger = Substitute.For<ILogger<DatabaseRepository>>();
        DatabaseRepository repository = new(db, repoLogger);

        ISubscriptionService resolvedSubscriptionService = subscriptionService ?? Substitute.For<ISubscriptionService>();
        TestServiceScopeFactory scopeFactory = new(db, new Dictionary<Type, object>
        {
            { typeof(ISubscriptionService), resolvedSubscriptionService },
            { typeof(IAlertRuleRepository), repository },
            { typeof(IAlertEventRepository), repository },
        });

        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        IAlertDeliveryService deliveryService = Substitute.For<IAlertDeliveryService>();
        ILogger<AlertEvaluationService> logger = Substitute.For<ILogger<AlertEvaluationService>>();

        AlertEvaluationService service = new(scopeFactory, distributedLock, redis, deliveryService, logger);

        return (service, db, redisDb, deliveryService, repository);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_ThresholdExceeded_NoDuration_CreatesAlertEvent()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 90, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        List<AlertEvent> events = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).ToListAsync();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Status).IsEqualTo(AlertEventStatus.Triggered);
        await Assert.That(events[0].MachineId).IsEqualTo(machine.Id);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_ThresholdExceeded_ActiveEventExists_NoNewEvent()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Seed an existing active alert event for this rule+machine.
        AlertEvent existingEvent = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id, status: AlertEventStatus.Triggered);
        existingEvent.Id = await db.InsertWithInt64IdentityAsync(existingEvent);

        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 95, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // Should still be just the one existing event.
        int eventCount = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).CountAsync();
        await Assert.That(eventCount).IsEqualTo(1);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_ConditionNotMet_ClearsRedisTrackingKey()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        AlertRule rule = TestDataBuilder.BuildAlertRule(metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 5);
        rule.Id = 1;

        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 50, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // Should delete the Redis condition tracking key.
        await redisDb.Received().KeyDeleteAsync($"alert:condition:{rule.Id}:{state.MachineId}");
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_DurationRequired_FirstOccurrence_StartsTrackingOnly()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 5);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Redis returns null for the condition key (first occurrence).
        string conditionKey = $"alert:condition:{rule.Id}:1";
        redisDb.StringGetAsync(conditionKey).Returns((RedisValue)RedisValue.Null);

        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 90, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // Should set the start time in Redis but NOT create an alert event.
        await redisDb.Received().StringSetAsync(conditionKey, Arg.Any<RedisValue>(), Arg.Is<Expiration>(e => e.Equals(new Expiration(TimeSpan.FromMinutes(35)))));
        int eventCount = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_DurationRequired_NotYetElapsed_DoesNotFire()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 5);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Redis returns a timestamp from 2 minutes ago (not yet past 5-minute duration).
        string conditionKey = $"alert:condition:{rule.Id}:1";
        string twoMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("o");
        redisDb.StringGetAsync(conditionKey).Returns((RedisValue)twoMinutesAgo);

        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 90, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // Should NOT create an alert event.
        int eventCount = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_DurationRequired_Elapsed_CreatesAlert()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 5);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Redis returns a timestamp from 10 minutes ago (past the 5-minute duration).
        string conditionKey = $"alert:condition:{rule.Id}:{machine.Id}";
        string tenMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("o");
        redisDb.StringGetAsync(conditionKey).Returns((RedisValue)tenMinutesAgo);

        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 90, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        List<AlertEvent> events = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).ToListAsync();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Status).IsEqualTo(AlertEventStatus.Triggered);
    }

    // --- WS-2: Condition Key TTL and Cleanup Tests ---

    [Test]
    public async Task EvaluateRuleForMachineAsync_DurationRule_SetsKeyWithAdequateTTL()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 10);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        string conditionKey = $"alert:condition:{rule.Id}:1";
        redisDb.StringGetAsync(conditionKey).Returns((RedisValue)RedisValue.Null);

        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 90, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // TTL should be DurationMinutes + 30 (not DurationMinutes + 5).
        await redisDb.Received().StringSetAsync(conditionKey, Arg.Any<RedisValue>(), Arg.Is<Expiration>(e => e.Equals(new Expiration(TimeSpan.FromMinutes(40)))));
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_DurationElapsed_DeletesConditionKey()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 5);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        string conditionKey = $"alert:condition:{rule.Id}:{machine.Id}";
        string tenMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("o");
        redisDb.StringGetAsync(conditionKey).Returns((RedisValue)tenMinutesAgo);

        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 90, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // Condition key should be deleted after the duration-elapsed alert fires.
        await redisDb.Received().KeyDeleteAsync(conditionKey);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_DurationNotElapsed_KeyPersists()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 10);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        string conditionKey = $"alert:condition:{rule.Id}:1";
        string twoMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("o");
        redisDb.StringGetAsync(conditionKey).Returns((RedisValue)twoMinutesAgo);

        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 90, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // Duration not yet elapsed — condition key should NOT be deleted.
        await redisDb.DidNotReceive().KeyDeleteAsync(conditionKey);
        int eventCount = await db.AlertEvents.CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_MetricNull_ReturnsWithoutEvaluation()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        // Use an unknown metric value that returns null.
        AlertRule rule = TestDataBuilder.BuildAlertRule(metric: (AlertMetric)99, threshold: 1m);
        rule.Id = 1;

        MachineStateSummary state = new() { MachineId = 1, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // Unknown metric returns null, so no Redis or DB interaction.
        await redisDb.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());
        int eventCount = await db.AlertEvents.CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }

    // --- MachineOffline Integration Tests ---

    [Test]
    public async Task EvaluateRuleForMachineAsync_MachineOfflineGreaterThan0_OfflineMachine_FiresAlert()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.MachineOffline, op: AlertOperator.GreaterThan, threshold: 0m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        MachineStateSummary state = new() { MachineId = machine.Id, HealthStatus = 3, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        List<AlertEvent> events = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).ToListAsync();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Status).IsEqualTo(AlertEventStatus.Triggered);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_MachineOfflineGreaterThan0_OnlineMachine_DoesNotFire()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.MachineOffline, op: AlertOperator.GreaterThan, threshold: 0m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        MachineStateSummary state = new() { MachineId = 1, HealthStatus = 0, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        int eventCount = await db.AlertEvents.CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_MachineOfflineWithDuration_StaysOffline_FiresAfterDuration()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.MachineOffline, op: AlertOperator.GreaterThan, threshold: 0m, durationMinutes: 5);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Simulate duration already elapsed via Redis key set 10 minutes ago.
        string conditionKey = $"alert:condition:{rule.Id}:{machine.Id}";
        string tenMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("o");
        redisDb.StringGetAsync(conditionKey).Returns((RedisValue)tenMinutesAgo);

        MachineStateSummary state = new() { MachineId = machine.Id, HealthStatus = 3, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        List<AlertEvent> events = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).ToListAsync();
        await Assert.That(events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_MachineOfflineWithDuration_ComesBackOnline_DoesNotFire()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.MachineOffline, op: AlertOperator.GreaterThan, threshold: 0m, durationMinutes: 5);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Machine came back online (HealthStatus = 0), condition no longer met.
        MachineStateSummary state = new() { MachineId = 1, HealthStatus = 0, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // Condition not met — should clear Redis key and not fire.
        await redisDb.Received().KeyDeleteAsync($"alert:condition:{rule.Id}:{state.MachineId}");
        int eventCount = await db.AlertEvents.CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_AlertCreated_EnqueuesDeliveryJob()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 95, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // Delivery is now enqueued via Redis, not called directly
        await delivery.Received(1).EnqueueAsync(Arg.Any<long>(), Arg.Is<int>(id => id == rule.Id), Arg.Is<int>(id => id == rule.TenantId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_AlertFires_DoesNotCallDeliverDirectly()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 95, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // DeliverAsync must not be called directly from evaluation; delivery is decoupled via queue
        await delivery.Received(0).DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());

        // EnqueueAsync should be called instead of DeliverAsync
        await delivery.Received(1).EnqueueAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_ZeroDuration_FiresImmediately()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.MemoryUsage, threshold: 50m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        MachineStateSummary state = new() { MachineId = machine.Id, MemoryUsagePercent = 75, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // Zero duration means fire immediately on first occurrence.
        int eventCount = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).CountAsync();
        await Assert.That(eventCount).IsEqualTo(1);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_DurationRequired_MalformedRedisTimestamp_ResetsKeyAndReturns()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 5);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Redis returns corrupted timestamp data — TryParse fails, key should be reset
        // defensively and no alert should fire (treated as a fresh start).
        string conditionKey = $"alert:condition:{rule.Id}:{machine.Id}";
        redisDb.StringGetAsync(conditionKey).Returns((RedisValue)"not-a-valid-date");

        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 90, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // Corrupted timestamp should reset the key and NOT fire an alert.
        await redisDb.Received().StringSetAsync(conditionKey, Arg.Any<RedisValue>(), Arg.Any<Expiration>());
        int eventCount = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }

    // --- EvaluateAllRulesAsync Tests ---

    [Test]
    public async Task EvaluateAllRulesAsync_NoEnabledRules_ReturnsEarly()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        // No rules seeded — should return immediately.
        await service.EvaluateAllRulesAsync(CancellationToken.None);

        await delivery.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateAllRulesAsync_FreeTierTenant_SkipsRules()
    {
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb(subscriptionService);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription freeSub = TestDataBuilder.BuildSubscription(tenantId: tenant.Id, tier: SubscriptionTier.Free, status: SubscriptionStatus.Active);
        await db.InsertWithInt32IdentityAsync(freeSub);

        subscriptionService.GetSubscriptionForTenantAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns(freeSub);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary machineState = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, cpuPercent: 95);
        await db.InsertAsync(machineState);

        await service.EvaluateAllRulesAsync(CancellationToken.None);

        // Free tier should be skipped — no alerts created.
        int eventCount = await db.AlertEvents.CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }

    [Test]
    public async Task EvaluateAllRulesAsync_NullSubscription_SkipsTenant()
    {
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb(subscriptionService);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        // Configure subscription service to return null.
        subscriptionService.GetSubscriptionForTenantAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns((TenantSubscription?)null);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        await service.EvaluateAllRulesAsync(CancellationToken.None);

        // Null subscription should be skipped.
        int eventCount = await db.AlertEvents.CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }

    [Test]
    public async Task EvaluateAllRulesAsync_InactiveSubscription_SkipsTenant()
    {
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb(subscriptionService);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription canceledSub = TestDataBuilder.BuildSubscription(tenantId: tenant.Id, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Canceled);

        subscriptionService.GetSubscriptionForTenantAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns(canceledSub);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        await service.EvaluateAllRulesAsync(CancellationToken.None);

        int eventCount = await db.AlertEvents.CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }

    [Test]
    public async Task EvaluateAllRulesAsync_DisabledRules_NotEvaluated()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        // Only disabled rules — should return early with no evaluation.
        AlertRule disabledRule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, isEnabled: false);
        disabledRule.Id = await db.InsertWithInt32IdentityAsync(disabledRule);

        await service.EvaluateAllRulesAsync(CancellationToken.None);

        await delivery.DidNotReceive().EnqueueAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // --- Cross-Cutting Tests ---

    [Test]
    public async Task EvaluateRuleForMachineAsync_AcknowledgedEvent_BlocksNewAlert()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Seed an existing Acknowledged (not Triggered, not Resolved) event.
        AlertEvent existingEvent = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id, status: AlertEventStatus.Acknowledged);
        existingEvent.AcknowledgedAt = DateTimeOffset.UtcNow;
        existingEvent.Id = await db.InsertWithInt64IdentityAsync(existingEvent);

        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 95, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // Acknowledged event should block a new alert from being created.
        int eventCount = await db.AlertEvents.Where(e => e.AlertRuleId == rule.Id).CountAsync();
        await Assert.That(eventCount).IsEqualTo(1);
    }

    [Test]
    public async Task EvaluateAllRulesAsync_ProTierBreachingMetric_CreatesAlert()
    {
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb(subscriptionService);

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription proSub = TestDataBuilder.BuildSubscription(tenantId: tenant.Id, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        await db.InsertWithInt32IdentityAsync(proSub);

        subscriptionService.GetSubscriptionForTenantAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns(proSub);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary machineState = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, cpuPercent: 95);
        await db.InsertAsync(machineState);

        await service.EvaluateAllRulesAsync(CancellationToken.None);

        int eventCount = await db.AlertEvents.CountAsync();
        await Assert.That(eventCount).IsEqualTo(1);

        await delivery.Received(1).EnqueueAsync(Arg.Any<long>(), Arg.Is<int>(id => id == rule.Id), Arg.Is<int>(id => id == tenant.Id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateAllRulesAsync_MultipleTenantsMultipleRules_EvaluatesCorrectly()
    {
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb(subscriptionService);

        // Tenant 1: one rule, one machine breaching
        Tenant tenant1 = TestDataBuilder.BuildTenant();
        tenant1.Id = await db.InsertWithInt32IdentityAsync(tenant1);
        TenantSubscription sub1 = TestDataBuilder.BuildSubscription(tenantId: tenant1.Id, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        await db.InsertWithInt32IdentityAsync(sub1);

        // Tenant 2: one rule, one machine breaching
        Tenant tenant2 = TestDataBuilder.BuildTenant();
        tenant2.Id = await db.InsertWithInt32IdentityAsync(tenant2);
        TenantSubscription sub2 = TestDataBuilder.BuildSubscription(tenantId: tenant2.Id, tier: SubscriptionTier.Team, status: SubscriptionStatus.Active);
        await db.InsertWithInt32IdentityAsync(sub2);

        subscriptionService.GetSubscriptionForTenantAsync(tenant1.Id, Arg.Any<CancellationToken>()).Returns(sub1);
        subscriptionService.GetSubscriptionForTenantAsync(tenant2.Id, Arg.Any<CancellationToken>()).Returns(sub2);

        AlertRule rule1 = TestDataBuilder.BuildAlertRule(tenantId: tenant1.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule1.Id = await db.InsertWithInt32IdentityAsync(rule1);
        AlertRule rule2 = TestDataBuilder.BuildAlertRule(tenantId: tenant2.Id, metric: AlertMetric.MemoryUsage, threshold: 50m, durationMinutes: 0);
        rule2.Id = await db.InsertWithInt32IdentityAsync(rule2);

        Machine machine1 = TestDataBuilder.BuildMachine(tenantId: tenant1.Id);
        machine1.Id = await db.InsertWithInt64IdentityAsync(machine1);
        Machine machine2 = TestDataBuilder.BuildMachine(tenantId: tenant2.Id);
        machine2.Id = await db.InsertWithInt64IdentityAsync(machine2);

        // Tenant 1: machine breaches CPU
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machine1.Id, tenantId: tenant1.Id, cpuPercent: 95));
        // Tenant 2: machine breaches memory
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machine2.Id, tenantId: tenant2.Id, memoryPercent: 75));

        await service.EvaluateAllRulesAsync(CancellationToken.None);

        // Exactly 2 events (one per tenant for the breaching machine).
        int eventCount = await db.AlertEvents.CountAsync();
        await Assert.That(eventCount).IsEqualTo(2);
    }

    // --- Auto-Resolution Tests ---

    [Test]
    public async Task EvaluateRuleForMachineAsync_ConditionClears_ResolvesTriggeredEvent()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Seed a Triggered event for this rule+machine
        AlertEvent triggeredEvent = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id, status: AlertEventStatus.Triggered);
        triggeredEvent.Id = await db.InsertWithInt64IdentityAsync(triggeredEvent);

        // CPU is now below threshold so the condition is no longer met
        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 50, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        AlertEvent? updatedEvent = await db.AlertEvents.FirstOrDefaultAsync(e => e.Id == triggeredEvent.Id);
        await Assert.That(updatedEvent).IsNotNull();
        await Assert.That(updatedEvent!.Status).IsEqualTo(AlertEventStatus.Resolved);
        await Assert.That(updatedEvent.ResolvedAt).IsNotNull();
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_ConditionClears_ResolvesAcknowledgedEvent()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Seed an Acknowledged event for this rule+machine
        AlertEvent acknowledgedEvent = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id, status: AlertEventStatus.Acknowledged);
        acknowledgedEvent.AcknowledgedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        acknowledgedEvent.Id = await db.InsertWithInt64IdentityAsync(acknowledgedEvent);

        // CPU below threshold so condition clears
        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 40, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        AlertEvent? updatedEvent = await db.AlertEvents.FirstOrDefaultAsync(e => e.Id == acknowledgedEvent.Id);
        await Assert.That(updatedEvent).IsNotNull();
        await Assert.That(updatedEvent!.Status).IsEqualTo(AlertEventStatus.Resolved);
        await Assert.That(updatedEvent.ResolvedAt).IsNotNull();
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_ConditionClears_AlreadyResolvedEvent_NoUpdate()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        // Seed an already-resolved event with a specific timestamp
        DateTimeOffset originalResolvedAt = DateTimeOffset.UtcNow.AddHours(-2);
        AlertEvent resolvedEvent = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id, status: AlertEventStatus.Resolved);
        resolvedEvent.ResolvedAt = originalResolvedAt;
        resolvedEvent.Id = await db.InsertWithInt64IdentityAsync(resolvedEvent);

        // CPU below threshold so condition is false
        MachineStateSummary state = new() { MachineId = machine.Id, CpuUsagePercent = 30, LastSeenAt = DateTimeOffset.UtcNow };

        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        // The resolved event should not have its ResolvedAt overwritten because the
        // UPDATE query filters on Status != Resolved
        AlertEvent? unchangedEvent = await db.AlertEvents.FirstOrDefaultAsync(e => e.Id == resolvedEvent.Id);
        await Assert.That(unchangedEvent).IsNotNull();
        await Assert.That(unchangedEvent!.Status).IsEqualTo(AlertEventStatus.Resolved);
        await Assert.That(unchangedEvent.ResolvedAt).IsEqualTo(originalResolvedAt);
    }

    [Test]
    public async Task EvaluateRuleForMachineAsync_ConditionClears_NoActiveEvents_NoError()
    {
        (AlertEvaluationService service, DatabaseContext db, IDatabase redisDb, IAlertDeliveryService delivery, IAlertEventRepository alertEventRepo) = CreateServiceWithDb();

        AlertRule rule = TestDataBuilder.BuildAlertRule(metric: AlertMetric.CpuUsage, threshold: 80m, durationMinutes: 0);
        rule.Id = 1;

        // No events exist in the database at all
        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 50, LastSeenAt = DateTimeOffset.UtcNow };

        // Should not throw when there are no events to resolve
        await service.EvaluateRuleForMachineAsync(alertEventRepo, rule, state, CancellationToken.None);

        int eventCount = await db.AlertEvents.CountAsync();
        await Assert.That(eventCount).IsEqualTo(0);
    }
}
