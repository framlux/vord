// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Alerts;

/// <summary>
/// Evaluates event-based alert rules at telemetry ingestion time.
/// </summary>
public sealed class EventAlertService : IEventAlertService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly ILogger<EventAlertService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="EventAlertService"/> class.
    /// </summary>
    public EventAlertService(
        IServiceScopeFactory scopeFactory,
        IAlertDeliveryService deliveryService,
        ILogger<EventAlertService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(deliveryService);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _deliveryService = deliveryService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task EvaluateSshConnectAsync(int tenantId, long machineId, string user, string sourceIp, int sourcePort, string authMethod, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ISubscriptionService subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

        TenantSubscription? subscription = await subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free) || (subscription.Status != SubscriptionStatus.Active))
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
}
