// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Telemetry;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Evaluates event-based alert rules at telemetry ingestion time.
/// </summary>
public sealed class EventAlertService : IEventAlertService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly AlertPipelineMetrics _alertPipelineMetrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventAlertService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="EventAlertService"/> class.
    /// </summary>
    /// <param name="scopeFactory">Factory for the per-call service scope.</param>
    /// <param name="deliveryService">Alert delivery enqueue.</param>
    /// <param name="alertPipelineMetrics">Alert pipeline metrics.</param>
    /// <param name="timeProvider">Clock the windowed evaluation measures back from.</param>
    /// <param name="logger">Logger.</param>
    public EventAlertService(
        IServiceScopeFactory scopeFactory,
        IAlertDeliveryService deliveryService,
        AlertPipelineMetrics alertPipelineMetrics,
        TimeProvider timeProvider,
        ILogger<EventAlertService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(deliveryService);
        ArgumentNullException.ThrowIfNull(alertPipelineMetrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _deliveryService = deliveryService;
        _alertPipelineMetrics = alertPipelineMetrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task EvaluateSshConnectAsync(int tenantId, long machineId, string user, string sourceIp, int sourcePort, string authMethod, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ISubscriptionService subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

        // Event alerts are a paid feature. A pre-processor cannot reach this path, so the gate is
        // asked of the shared policy rather than restated here.
        TenantSubscription? subscription = await subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
        if (SubscriptionPolicy.RequiresPro(subscription))
        {
            return;
        }

        IAlertRuleRepository alertRuleRepo = scope.ServiceProvider.GetRequiredService<IAlertRuleRepository>();
        List<AlertRule> rules = await alertRuleRepo.GetEnabledRulesForMachineByMetricAsync(tenantId, machineId, AlertMetric.SshConnection, ct);

        if (rules.Count == 0)
        {
            return;
        }

        IAlertEventRepository alertEventRepo = scope.ServiceProvider.GetRequiredService<IAlertEventRepository>();

        foreach (AlertRule rule in rules)
        {
            // A custom rule is Team's to run as well as to author. The downgrade paths clear its
            // enabled flag, but that flag is a stored bit several writers have to maintain, and this
            // asks the tier instead of trusting them.
            if (SubscriptionPolicy.RefusesAlertRule(rule, subscription))
            {
                continue;
            }

            string message = $"New SSH connection: user {user} from {sourceIp} ({authMethod})";
            string details = JsonSerializer.Serialize(
                new { user, sourceIp, sourcePort, authMethod },
                JsonDefaults.CamelCase);

            AlertEvent alertEvent = new()
            {
                AlertRuleId = rule.Id,
                TenantId = tenantId,
                MachineId = machineId,
                Severity = rule.Severity,
                Message = message,
                Details = details,
                Status = AlertEventStatus.Triggered,
                TriggeredAt = DateTimeOffset.UtcNow,
            };

            AlertEvent? createdEvent = await alertEventRepo.CreateEventIfNotExistsAsync(alertEvent, ct);

            if (createdEvent is null)
            {
                continue;
            }

            _logger.LogInformation(
                "SSH alert triggered: Rule {RuleId} for machine {MachineId} — user {User} from {SourceIp}",
                rule.Id, machineId, user, sourceIp);

            _alertPipelineMetrics.RecordEventFired(rule.Metric, rule.Severity);
            await _deliveryService.EnqueueAsync(createdEvent.Id, rule.Id, tenantId, ct);
        }
    }

    /// <inheritdoc/>
    public async Task ResolveSshDisconnectAsync(long machineId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IAlertEventRepository alertEventRepo = scope.ServiceProvider.GetRequiredService<IAlertEventRepository>();

        await alertEventRepo.ResolveEventsForMachineByMetricAsync(machineId, AlertMetric.SshConnection, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> EvaluateFailedSshLoginWindowAsync(int tenantId, long machineId, DateTimeOffset windowOpenedAt, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ISubscriptionService subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

        TenantSubscription? subscription = await subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
        if (SubscriptionPolicy.RequiresPro(subscription))
        {
            return false;
        }

        IAlertRuleRepository alertRuleRepo = scope.ServiceProvider.GetRequiredService<IAlertRuleRepository>();
        List<AlertRule> rules = await alertRuleRepo.GetEnabledRulesForMachineByMetricAsync(tenantId, machineId, AlertMetric.FailedSshLogin, ct);

        if (rules.Count == 0)
        {
            return false;
        }

        IAlertEventRepository alertEventRepo = scope.ServiceProvider.GetRequiredService<IAlertEventRepository>();
        IMachineStateRepository machineStateRepo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

        // Every rule's window ends here, at the moment the evaluation runs, rather than at a bucket
        // boundary in the past. A job that runs late therefore still looks at the last N minutes and
        // can never resolve through failures that arrived after a boundary it was scheduled for.
        DateTimeOffset windowEnd = _timeProvider.GetUtcNow();
        bool stillLive = false;

        foreach (AlertRule rule in rules)
        {
            if (SubscriptionPolicy.RefusesAlertRule(rule, subscription))
            {
                continue;
            }

            int windowMinutes = (rule.DurationMinutes > 0) ? rule.DurationMinutes : AlertConstants.FailedSshLoginWindowMinutes;
            DateTimeOffset windowStart = ResolveWindowStart(windowEnd, windowMinutes, windowOpenedAt);

            int failureCount = await machineStateRepo.CountTelemetryWithPayloadMarkerAsync(
                machineId,
                TelemetryTypeIds.SshSessions,
                AlertConstants.FailedSshLoginPayloadMarker,
                windowStart,
                windowEnd,
                ct);

            if (failureCount == 0)
            {
                // Only a window with nothing in it resolves, and it resolves this rule alone. The
                // per-metric resolve would clear a longer rule's still-live incident on the strength
                // of a shorter rule's quiet window.
                await alertEventRepo.ResolveEventsForRuleMachineAsync(rule.Id, machineId, ct);

                continue;
            }

            // Failures are still arriving, so the chain must continue even when this window is under
            // threshold. A chain that stopped here would leave any open event Triggered forever, and
            // the de-duplication would then swallow every later incident on this rule and machine.
            stillLive = true;

            if (AlertEvaluationJob.EvaluateCondition(failureCount, rule.Operator, rule.Threshold) == false)
            {
                continue;
            }

            // Every re-armed run of a live incident is still over threshold, so without this the
            // summary below would read and deserialize its whole payload sample on every run only for
            // the insert's de-duplication to discard the result. The insert re-checks under its
            // advisory lock, so this is purely a way to not pay for an answer already known.
            if (await alertEventRepo.HasActiveEventForRuleMachineAsync(rule.Id, machineId, ct) == true)
            {
                continue;
            }

            FailedSshLoginWindowSummary summary = await BuildWindowSummaryAsync(
                machineStateRepo, machineId, failureCount, windowMinutes, windowStart, windowEnd, ct);

            string message = $"{failureCount} failed SSH logins in {windowMinutes} minute(s) — most attempts from {summary.TopSourceIp}";
            string details = JsonSerializer.Serialize(summary, JsonDefaults.CamelCase);

            AlertEvent alertEvent = new()
            {
                AlertRuleId = rule.Id,
                TenantId = tenantId,
                MachineId = machineId,
                Severity = rule.Severity,
                Message = message,
                Details = details,
                Status = AlertEventStatus.Triggered,
                TriggeredAt = windowEnd,
            };

            AlertEvent? createdEvent = await alertEventRepo.CreateEventIfNotExistsAsync(alertEvent, ct);

            if (createdEvent is null)
            {
                continue;
            }

            _logger.LogInformation(
                "Failed SSH login alert triggered: Rule {RuleId} for machine {MachineId} — {FailureCount} attempts in {WindowMinutes} minutes from {DistinctSourceIpCount} address(es)",
                rule.Id, machineId, failureCount, windowMinutes, summary.DistinctSourceIpCount);

            _alertPipelineMetrics.RecordEventFired(rule.Metric, rule.Severity);
            await _deliveryService.EnqueueAsync(createdEvent.Id, rule.Id, tenantId, ct);
        }

        return stillLive;
    }

    /// <summary>
    /// Returns where a rule's window starts, given when the evaluation runs and when the window it is
    /// answering was opened.
    /// </summary>
    /// <remarks>
    /// A job scheduled one window ahead runs at that instant plus queue latency, so a window measured
    /// purely back from the run instant starts strictly after the telemetry that opened it. Every row
    /// in a telemetry envelope carries one server receipt timestamp, so a burst delivered in a single
    /// envelope falls entirely outside such a window and the incident is never seen. Anchoring to the
    /// moment the window opened closes that hole, and widening backwards can only add rows, so a late
    /// run still cannot resolve through failures that arrived after the boundary it was scheduled for.
    /// The slack bound stops a run recovered from a long backlog counting hours of attempts against a
    /// five-minute rule.
    /// </remarks>
    internal static DateTimeOffset ResolveWindowStart(DateTimeOffset windowEnd, int windowMinutes, DateTimeOffset windowOpenedAt)
    {
        DateTimeOffset nominalStart = windowEnd.AddMinutes(-windowMinutes);

        if (windowOpenedAt >= nominalStart)
        {
            return nominalStart;
        }

        DateTimeOffset earliestStart = nominalStart - AlertConstants.FailedSshLoginMaxWindowSlack;

        return (windowOpenedAt < earliestStart) ? earliestStart : windowOpenedAt;
    }

    private static async Task<FailedSshLoginWindowSummary> BuildWindowSummaryAsync(
        IMachineStateRepository machineStateRepo,
        long machineId,
        int failureCount,
        int windowMinutes,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken ct)
    {
        // Reached only once the count has decided an incident is happening AND no event is already
        // open for it, so the row read is bounded by how often an alert fires rather than by how
        // fast an attacker tries or how long the incident lasts.
        List<string> payloads = await machineStateRepo.GetTelemetryPayloadsWithMarkerAsync(
            machineId,
            TelemetryTypeIds.SshSessions,
            AlertConstants.FailedSshLoginPayloadMarker,
            windowStart,
            windowEnd,
            AlertConstants.FailedSshLoginDetailSampleLimit,
            ct);

        List<SshSessionPayload> attempts = [];

        foreach (string payload in payloads)
        {
            SshSessionPayload? parsed = JsonSerializer.Deserialize<SshSessionPayload>(payload, JsonDefaults.SnakeCase);

            if (parsed is not null)
            {
                attempts.Add(parsed);
            }
        }

        return FailedSshLoginWindowSummary.Create(failureCount, windowMinutes, windowStart, windowEnd, attempts);
    }
}
