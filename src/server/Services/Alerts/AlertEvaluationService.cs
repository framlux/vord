// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Alerts;

/// <summary>
/// Background service that evaluates alert rules against machine state on a periodic basis.
/// </summary>
public sealed class AlertEvaluationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedLock _distributedLock;
    private readonly IConnectionMultiplexer _redis;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly ILogger<AlertEvaluationService> _logger;

    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Creates a new instance of the <see cref="AlertEvaluationService"/> class.
    /// </summary>
    public AlertEvaluationService(
        IServiceScopeFactory scopeFactory,
        IDistributedLock distributedLock,
        IConnectionMultiplexer redis,
        IAlertDeliveryService deliveryService,
        ILogger<AlertEvaluationService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(distributedLock);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(deliveryService);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _distributedLock = distributedLock;
        _redis = redis;
        _deliveryService = deliveryService;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                LockHandle? lockHandle = await _distributedLock.TryAcquireAsync("alert-evaluation-lock", LockTtl);
                if (lockHandle is null)
                {
                    await Task.Delay(EvaluationInterval, stoppingToken);

                    continue;
                }

                await using (lockHandle)
                {
                    await EvaluateAllRulesAsync(stoppingToken);
                }

                await Task.Delay(EvaluationInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during alert evaluation cycle");
            }
        }
    }

    internal async Task EvaluateAllRulesAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IMachineStateRepository machineStateRepo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();
        IAlertRuleRepository alertRuleRepo = scope.ServiceProvider.GetRequiredService<IAlertRuleRepository>();
        IAlertEventRepository alertEventRepo = scope.ServiceProvider.GetRequiredService<IAlertEventRepository>();
        ISubscriptionService subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

        List<AlertRule> enabledRules = await alertRuleRepo.GetEnabledAlertRulesAsync(ct);

        if (enabledRules.Count == 0)
        {

            return;
        }

        // Group rules by tenant
        IEnumerable<IGrouping<int, AlertRule>> rulesByTenant = enabledRules.GroupBy(r => r.TenantId);

        foreach (IGrouping<int, AlertRule> tenantRules in rulesByTenant)
        {
            int tenantId = tenantRules.Key;

            // Only evaluate for Pro+ subscriptions
            TenantSubscription? subscription = await subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
            if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free) || (subscription.Status != SubscriptionStatus.Active))
            {
                continue;
            }

            List<MachineStateSummary> machineStates = await machineStateRepo.GetSummariesForTenantMachinesAsync(tenantId, ct);

            foreach (AlertRule rule in tenantRules)
            {
                foreach (MachineStateSummary state in machineStates)
                {
                    await EvaluateRuleForMachineAsync(alertEventRepo, rule, state, ct);
                }
            }
        }
    }

    internal async Task EvaluateRuleForMachineAsync(IAlertEventRepository alertEventRepo, AlertRule rule, MachineStateSummary state, CancellationToken ct)
    {
        decimal? currentValue = GetMetricValue(rule.Metric, state);
        if (currentValue is null)
        {

            return;
        }

        bool conditionMet = EvaluateCondition(currentValue.Value, rule.Operator, rule.Threshold);

        if (conditionMet == false)
        {
            // Clear any tracked condition start time
            IDatabase redisDb = _redis.GetDatabase();
            await redisDb.KeyDeleteAsync($"{AlertConstants.ConditionKeyPrefix}:{rule.Id}:{state.MachineId}");

            // Auto-resolve any active events for this rule and machine
            await alertEventRepo.ResolveEventsForRuleMachineAsync(rule.Id, state.MachineId, ct);

            return;
        }

        // Check if condition has persisted long enough
        if (rule.DurationMinutes > 0)
        {
            IDatabase redisDb = _redis.GetDatabase();
            string conditionKey = $"{AlertConstants.ConditionKeyPrefix}:{rule.Id}:{state.MachineId}";
            string? startTimeStr = await redisDb.StringGetAsync(conditionKey);

            if (startTimeStr is null)
            {
                await redisDb.StringSetAsync(conditionKey, DateTimeOffset.UtcNow.ToString("o"), TimeSpan.FromMinutes(rule.DurationMinutes + 30));

                return;
            }

            if (DateTimeOffset.TryParse(startTimeStr, out DateTimeOffset conditionStart) == false)
            {
                // Corrupted timestamp — reset the tracking key and treat as a fresh start.
                await redisDb.StringSetAsync(conditionKey, DateTimeOffset.UtcNow.ToString("o"), TimeSpan.FromMinutes(rule.DurationMinutes + 30));

                return;
            }

            TimeSpan elapsed = DateTimeOffset.UtcNow - conditionStart;
            if (elapsed.TotalMinutes < rule.DurationMinutes)
            {

                return;
            }
        }

        // Create alert event with advisory lock deduplication protection.
        AlertEvent alertEvent = new()
        {
            AlertRuleId = rule.Id,
            TenantId = rule.TenantId,
            MachineId = state.MachineId,
            Severity = rule.Severity,
            Message = $"{rule.Name}: {rule.Metric} is {currentValue.Value} (threshold: {rule.Operator} {rule.Threshold})",
            Details = JsonSerializer.Serialize(new { metric = rule.Metric.ToString(), currentValue, threshold = rule.Threshold }, JsonDefaults.CamelCase),
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };

        AlertEvent? createdEvent = await alertEventRepo.CreateEventIfNotExistsAsync(alertEvent, ct);

        if (createdEvent is null)
        {
            // An active event already exists for this rule and machine; skip delivery.
            return;
        }

        _logger.LogInformation("Alert triggered: Rule {RuleId} ({RuleName}) for machine {MachineId}", rule.Id, rule.Name, state.MachineId);

        // Delete the condition key after a duration-elapsed alert fires so that if the
        // condition clears and re-triggers, the duration window starts fresh.
        if (rule.DurationMinutes > 0)
        {
            IDatabase redisDb = _redis.GetDatabase();
            await redisDb.KeyDeleteAsync($"{AlertConstants.ConditionKeyPrefix}:{rule.Id}:{state.MachineId}");
        }

        await _deliveryService.EnqueueAsync(createdEvent.Id, rule.Id, rule.TenantId, ct);
    }

    internal static decimal? GetMetricValue(AlertMetric metric, MachineStateSummary state)
    {
        return metric switch
        {
            AlertMetric.CpuUsage => state.CpuUsagePercent.HasValue ? (decimal)state.CpuUsagePercent.Value : null,
            AlertMetric.MemoryUsage => state.MemoryUsagePercent.HasValue ? (decimal)state.MemoryUsagePercent.Value : null,
            AlertMetric.DiskUsage => GetMaxDiskUsage(state),
            AlertMetric.FailedServices => state.FailedServices.HasValue ? (decimal)state.FailedServices.Value : null,
            AlertMetric.SecurityUpdates => state.SecurityUpdates.HasValue ? (decimal)state.SecurityUpdates.Value : null,
            AlertMetric.DiskHealth => GetDiskHealthValue(state),
            AlertMetric.MachineOffline => state.HealthStatus == AlertConstants.HealthStatusOffline ? 1m : 0m,
            _ => null,
        };
    }

    internal static decimal? GetMaxDiskUsage(MachineStateSummary state)
    {
        if (state.MaxDiskUsagePercent.HasValue == false)
        {
            return null;
        }

        return (decimal)state.MaxDiskUsagePercent.Value;
    }

    internal static decimal? GetDiskHealthValue(MachineStateSummary state)
    {
        if (state.HasDiskHealthIssue.HasValue == false)
        {
            return null;
        }

        return state.HasDiskHealthIssue.Value ? 1 : 0;
    }

    internal static bool EvaluateCondition(decimal value, AlertOperator op, decimal threshold)
    {
        return op switch
        {
            AlertOperator.GreaterThan => value > threshold,
            AlertOperator.LessThan => value < threshold,
            AlertOperator.EqualTo => value == threshold,
            _ => false,
        };
    }
}
