// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Hangfire fire-and-forget job that delivers a single alert event to its integration endpoint(s).
/// Replaces the former <c>IntegrationDeliveryWorkerService</c> + Redis list queue: failures retry
/// via Hangfire's <see cref="AutomaticRetryAttribute"/> and surface in the dashboard's Failed tab
/// when retries are exhausted.
/// </summary>
public sealed class IntegrationDeliveryJob
{
    private readonly IAlertEventRepository _alertEventRepository;
    private readonly IAlertRuleRepository _alertRuleRepository;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly ILogger<IntegrationDeliveryJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="IntegrationDeliveryJob"/> class.
    /// </summary>
    public IntegrationDeliveryJob(
        IAlertEventRepository alertEventRepository,
        IAlertRuleRepository alertRuleRepository,
        IAlertDeliveryService deliveryService,
        ILogger<IntegrationDeliveryJob> logger)
    {
        ArgumentNullException.ThrowIfNull(alertEventRepository);
        ArgumentNullException.ThrowIfNull(alertRuleRepository);
        ArgumentNullException.ThrowIfNull(deliveryService);
        ArgumentNullException.ThrowIfNull(logger);

        _alertEventRepository = alertEventRepository;
        _alertRuleRepository = alertRuleRepository;
        _deliveryService = deliveryService;
        _logger = logger;
    }

    /// <summary>
    /// Delivers the alert event identified by <paramref name="eventId"/> via every configured
    /// integration endpoint for the rule's tenant. Throws on transient delivery failure so Hangfire
    /// retries; returns cleanly for permanent misses (event or rule deleted) so the retry budget
    /// is not consumed on data that will never reappear.
    /// </summary>
    /// <param name="eventId">The alert event id (must be positive).</param>
    /// <param name="ruleId">The alert rule id (must be positive).</param>
    /// <param name="tenantId">The tenant id (must be positive; informational, used for diagnostics).</param>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new int[] { 10, 20, 40 })]
    public async Task DeliverAsync(long eventId, int ruleId, int tenantId, CancellationToken ct)
    {
        if (eventId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventId), "Event id must be positive.");
        }

        if (ruleId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ruleId), "Rule id must be positive.");
        }

        if (tenantId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tenantId), "Tenant id must be positive.");
        }

        AlertEvent? alertEvent = await _alertEventRepository.GetAlertEventByIdAsync(eventId, ct);
        if (alertEvent is null)
        {
            // Event was deleted between enqueue and delivery. Throwing would cause Hangfire to
            // retry against data that will never come back; log and exit instead.
            _logger.LogWarning("Alert event {EventId} not found for delivery — skipping", eventId);

            return;
        }

        AlertRule? rule = await _alertRuleRepository.GetAlertRuleByIdAsync(ruleId, ct);
        if (rule is null)
        {
            _logger.LogWarning("Alert rule {RuleId} not found for delivery of event {EventId} — skipping", ruleId, eventId);

            return;
        }

        await _deliveryService.DeliverAsync(alertEvent, rule, ct);
    }
}
