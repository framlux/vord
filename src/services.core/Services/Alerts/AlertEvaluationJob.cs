// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Hangfire recurring job that evaluates threshold-based alert rules against current machine state
/// on a per-minute cadence. Replaces the former <c>AlertEvaluationService</c>. Condition-duration
/// tracking that previously lived in Redis (<c>alert:condition:*</c> keys) now lives in the
/// <see cref="AlertConditionState"/> table accessed via <see cref="IAlertConditionStateRepository"/>.
/// </summary>
public sealed class AlertEvaluationJob
{
    private readonly IMachineStateRepository _machineStateRepository;
    private readonly IAlertRuleRepository _alertRuleRepository;
    private readonly IAlertEventRepository _alertEventRepository;
    private readonly IAlertConditionStateRepository _alertConditionStateRepository;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly ILogger<AlertEvaluationJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertEvaluationJob"/> class.
    /// </summary>
    public AlertEvaluationJob(
        IMachineStateRepository machineStateRepository,
        IAlertRuleRepository alertRuleRepository,
        IAlertEventRepository alertEventRepository,
        IAlertConditionStateRepository alertConditionStateRepository,
        ISubscriptionService subscriptionService,
        IAlertDeliveryService deliveryService,
        ILogger<AlertEvaluationJob> logger)
    {
        ArgumentNullException.ThrowIfNull(machineStateRepository);
        ArgumentNullException.ThrowIfNull(alertRuleRepository);
        ArgumentNullException.ThrowIfNull(alertEventRepository);
        ArgumentNullException.ThrowIfNull(alertConditionStateRepository);
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(deliveryService);
        ArgumentNullException.ThrowIfNull(logger);

        _machineStateRepository = machineStateRepository;
        _alertRuleRepository = alertRuleRepository;
        _alertEventRepository = alertEventRepository;
        _alertConditionStateRepository = alertConditionStateRepository;
        _subscriptionService = subscriptionService;
        _deliveryService = deliveryService;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates every enabled, threshold-based alert rule against the latest machine state and
    /// enqueues delivery for any new alert events.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("critical")]
    public async Task RunAsync(CancellationToken ct)
    {
        List<AlertRule> enabledRules = await _alertRuleRepository.GetEnabledAlertRulesAsync(ct);

        if (enabledRules.Count == 0)
        {
            return;
        }

        IEnumerable<IGrouping<int, AlertRule>> rulesByTenant = enabledRules.GroupBy(r => r.TenantId);

        foreach (IGrouping<int, AlertRule> tenantRules in rulesByTenant)
        {
            int tenantId = tenantRules.Key;

            // Per-tenant fault isolation: one tenant's bad data (e.g., subscription lookup throws,
            // or a stale machine state row trips a downstream call) must not abort the entire
            // evaluation cycle. Log and move to the next tenant. The job retries every minute via
            // the recurring schedule, so transient failures self-heal.
            try
            {
                // Threshold-based alerts are a paid feature. Free tier (and any non-active status) skip.
                TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
                if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free) || (subscription.Status != SubscriptionStatus.Active))
                {
                    continue;
                }

                List<int> ruleIds = tenantRules.Select(r => r.Id).ToList();
                Dictionary<int, List<long>> machinesByRule = await _alertRuleRepository.GetMachineIdsForRulesAsync(ruleIds, ct);

                List<MachineStateSummary> machineStates = await _machineStateRepository.GetSummariesForTenantMachinesAsync(tenantId, ct);

                foreach (AlertRule rule in tenantRules)
                {
                    // Event-based metrics are evaluated at telemetry ingestion time, not here.
                    if (AlertConstants.IsEventMetric(rule.Metric))
                    {
                        continue;
                    }

                    List<long> assignedMachineIds = machinesByRule.GetValueOrDefault(rule.Id, []);
                    if (assignedMachineIds.Count == 0)
                    {
                        _logger.LogDebug("Alert rule {RuleId} has no assigned machines, skipping", rule.Id);

                        continue;
                    }

                    HashSet<long> assignedSet = new(assignedMachineIds);
                    List<MachineStateSummary> scopedStates = machineStates
                        .Where(s => assignedSet.Contains(s.MachineId))
                        .ToList();

                    foreach (MachineStateSummary state in scopedStates)
                    {
                        await EvaluateRuleForMachineAsync(rule, state, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown — propagate so the recurring run records as cancelled.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alert evaluation failed for tenant {TenantId}; continuing with remaining tenants", tenantId);
            }
        }
    }

    internal async Task EvaluateRuleForMachineAsync(AlertRule rule, MachineStateSummary state, CancellationToken ct)
    {
        decimal? currentValue = GetMetricValue(rule.Metric, state);
        if (currentValue is null)
        {
            return;
        }

        bool conditionMet = EvaluateCondition(currentValue.Value, rule.Operator, rule.Threshold);

        if (conditionMet == false)
        {
            // Clear any tracked condition start time for this rule+machine.
            await _alertConditionStateRepository.DeleteAsync(rule.Id, state.MachineId, ct);

            // Auto-resolve any active events for this rule and machine.
            await _alertEventRepository.ResolveEventsForRuleMachineAsync(rule.Id, state.MachineId, ct);

            return;
        }

        // Sample the clock once so the duration check and the persisted TriggeredAt timestamp share
        // the same observation instant.
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Check if condition has persisted long enough.
        if (rule.DurationMinutes > 0)
        {
            DateTimeOffset firstTriggered = await _alertConditionStateRepository.UpsertObservationAsync(
                rule.Id, state.MachineId, now, ct);

            TimeSpan elapsed = now - firstTriggered;
            if (elapsed.TotalMinutes < rule.DurationMinutes)
            {
                return;
            }
        }

        AlertEvent alertEvent = new()
        {
            AlertRuleId = rule.Id,
            TenantId = rule.TenantId,
            MachineId = state.MachineId,
            Severity = rule.Severity,
            Message = $"{rule.Name}: {rule.Metric} is {currentValue.Value} (threshold: {rule.Operator} {rule.Threshold})",
            Details = JsonSerializer.Serialize(new { metric = rule.Metric.ToString(), currentValue, threshold = rule.Threshold }, JsonDefaults.CamelCase),
            Status = AlertEventStatus.Triggered,
            TriggeredAt = now,
        };

        AlertEvent? createdEvent = await _alertEventRepository.CreateEventIfNotExistsAsync(alertEvent, ct);

        if (createdEvent is null)
        {
            // An active event already exists for this rule and machine; skip delivery.
            return;
        }

        _logger.LogInformation("Alert triggered: Rule {RuleId} ({RuleName}) for machine {MachineId}", rule.Id, rule.Name, state.MachineId);

        // Delete the condition state row after a duration-elapsed alert fires so that if the
        // condition clears and re-triggers, the duration window starts fresh.
        if (rule.DurationMinutes > 0)
        {
            await _alertConditionStateRepository.DeleteAsync(rule.Id, state.MachineId, ct);
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
            AlertMetric.SshConnection => null,
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
