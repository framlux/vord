// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
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
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(5);

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
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        ISubscriptionService subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

        List<AlertRule> enabledRules = await db.AlertRules
            .Where(r => r.IsEnabled)
            .ToListAsync(ct);

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

            List<MachineStateSummary> machineStates = await db.MachineStateSummaries
                .Where(s => db.Machines.Any(m => m.Id == s.MachineId && m.TenantId == tenantId && m.IsDeleted == false))
                .ToListAsync(ct);

            foreach (AlertRule rule in tenantRules)
            {
                foreach (MachineStateSummary state in machineStates)
                {
                    await EvaluateRuleForMachineAsync(db, rule, state, ct);
                }
            }
        }
    }

    internal async Task EvaluateRuleForMachineAsync(DatabaseContext db, AlertRule rule, MachineStateSummary state, CancellationToken ct)
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
            await redisDb.KeyDeleteAsync($"alert:condition:{rule.Id}:{state.MachineId}");

            return;
        }

        // Check if condition has persisted long enough
        if (rule.DurationMinutes > 0)
        {
            IDatabase redisDb = _redis.GetDatabase();
            string conditionKey = $"alert:condition:{rule.Id}:{state.MachineId}";
            string? startTimeStr = await redisDb.StringGetAsync(conditionKey);

            if (startTimeStr is null)
            {
                await redisDb.StringSetAsync(conditionKey, DateTimeOffset.UtcNow.ToString("o"), TimeSpan.FromMinutes(rule.DurationMinutes + 5));

                return;
            }

            if (DateTimeOffset.TryParse(startTimeStr, out DateTimeOffset conditionStart))
            {
                TimeSpan elapsed = DateTimeOffset.UtcNow - conditionStart;
                if (elapsed.TotalMinutes < rule.DurationMinutes)
                {
                    return;
                }
            }
        }

        // Check if there's already an active (non-resolved) event for this rule+machine
        bool hasActiveEvent = await db.AlertEvents
            .AnyAsync(e => e.AlertRuleId == rule.Id &&
                          e.MachineId == state.MachineId &&
                          e.Status != AlertEventStatus.Resolved, ct);

        if (hasActiveEvent)
        {
            return;
        }

        // Create alert event
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

        alertEvent.Id = await db.InsertWithInt64IdentityAsync(alertEvent, token: ct);
        _logger.LogInformation("Alert triggered: Rule {RuleId} ({RuleName}) for machine {MachineId}", rule.Id, rule.Name, state.MachineId);

        await _deliveryService.DeliverAsync(alertEvent, rule, ct);
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
            AlertMetric.MachineOffline => null, // Handled separately via ping service
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
