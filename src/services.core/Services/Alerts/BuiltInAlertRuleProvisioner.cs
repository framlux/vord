// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Provisions and enables the built-in alert rules described by <see cref="BuiltInAlertRuleDefinitions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Provisioning lives here, and is called from tenant creation, because there are three routes to a
/// paid tier and the previous arrangement — a seeder inlined in the Stripe checkout webhook — had to
/// be remembered at each of them. It was not. Seeding at creation means the rules exist for every
/// tenant regardless of how it arrived, and the entitlement flag is the only thing a tier change has
/// to move.
/// </para>
/// <para>
/// Entitlement is read from <see cref="ISubscriptionService"/> rather than from the subscription row
/// directly, because a self-hosted deployment leaves the stored row on Free forever and synthesises
/// Team through a decorator. Reading the row would seed every self-hosted tenant's rules off
/// permanently, with no billing event ever arriving to turn them back on.
/// </para>
/// <para>
/// A missing subscription counts as unentitled. A tenant provisioned inside its own creation
/// transaction may have no subscription row visible yet, and seeding disabled is the recoverable
/// direction — the enable path runs on every entitled transition, whereas nothing later would turn a
/// wrongly-enabled rule off.
/// </para>
/// <para>
/// Rules are provisioned unassigned. An unassigned rule watches no machines and therefore fires for
/// nothing until the tenant chooses its coverage; that choice is deliberately the tenant's.
/// </para>
/// </remarks>
public sealed class BuiltInAlertRuleProvisioner : IBuiltInAlertRuleProvisioner
{
    private readonly IAlertRuleRepository _alertRuleRepository;
    private readonly ILogger<BuiltInAlertRuleProvisioner> _logger;
    private readonly ISubscriptionService _subscriptionService;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="BuiltInAlertRuleProvisioner"/> class.
    /// </summary>
    /// <param name="alertRuleRepository">Reads the metrics already present and writes the missing rules.</param>
    /// <param name="subscriptionService">Answers whether the tenant is entitled to run the rules.</param>
    /// <param name="timeProvider">
    /// Clock stamping created and updated timestamps. These are rendered on the alerts page, so they
    /// must be a real time rather than a default.
    /// </param>
    /// <param name="logger">Logger.</param>
    public BuiltInAlertRuleProvisioner(
        IAlertRuleRepository alertRuleRepository,
        ISubscriptionService subscriptionService,
        TimeProvider timeProvider,
        ILogger<BuiltInAlertRuleProvisioner> logger)
    {
        ArgumentNullException.ThrowIfNull(alertRuleRepository);
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _alertRuleRepository = alertRuleRepository;
        _subscriptionService = subscriptionService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task EnsureProvisionedAsync(int tenantId, CancellationToken ct = default)
    {
        List<AlertMetric> existing = await _alertRuleRepository.GetBuiltInMetricsForTenantAsync(tenantId, ct);
        HashSet<AlertMetric> present = [.. existing];

        List<BuiltInAlertRuleDefinition> missing =
            [.. BuiltInAlertRuleDefinitions.All.Where(d => present.Contains(d.Metric) == false)];

        if (missing.Count == 0)
        {
            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
        bool entitled = SubscriptionPolicy.RequiresPro(subscription) == false;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        List<AlertRule> rules = [.. missing.Select(d => new AlertRule
        {
            TenantId = tenantId,
            Name = d.Name,
            Metric = d.Metric,
            Operator = d.Operator,
            Threshold = d.Threshold,
            DurationMinutes = d.DurationMinutes,
            Severity = d.Severity,
            IsEnabled = entitled,
            NotifyEmail = true,
            NotifyWebhook = false,
            IsCustom = false,
            CreatedByUserId = BuiltInAlertRuleDefinitions.SystemUserId,
            CreatedAt = now,
            UpdatedAt = now,
        })];

        try
        {
            await _alertRuleRepository.InsertAlertRulesAsync(rules, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Another caller seeded concurrently and the unique index refused the duplicate. Reachable
            // only from the backstop calls — the billing webhook, the administrative grant, the tier
            // correction — because the tenant-creation call holds an id nothing else has seen yet, so
            // this can never mask a poisoned ambient transaction.
            //
            // Note the insert writes one row at a time in a loop, so losing this race can leave a
            // partial set: five of eight rows written and the sixth refused. "Eight rows or none" is
            // not an invariant. It is self-healing — the next call to this method reads the metrics
            // that landed and inserts only what is still missing.
            _logger.LogInformation(
                "Built-in alert rules already provisioned concurrently for tenant {TenantId}", tenantId);

            return;
        }

        _logger.LogInformation(
            "Provisioned {Count} built-in alert rules for tenant {TenantId} (enabled: {Enabled})",
            rules.Count, tenantId, entitled);
    }

    /// <inheritdoc/>
    public async Task RestoreForTierAsync(
        int tenantId,
        TenantSubscription? priorSubscription,
        SubscriptionTier currentTier,
        SubscriptionStatus currentStatus,
        CancellationToken ct = default)
    {
        if ((currentTier == SubscriptionTier.Free) || (currentStatus != SubscriptionStatus.Active))
        {
            return;
        }

        // Seeding never mutates an existing row, so it runs on every entitled transition regardless of
        // what is restored — it is the backstop for a paying tenant that was somehow never provisioned.
        await EnsureProvisionedAsync(tenantId, ct);

        // Rules are disabled by exactly one mechanism: the downgrade sweep, which runs when a tenant
        // lands on Free or has its account cancelled. So there is something to turn back on only when
        // the tenant is arriving from one of those two states. A renewal, a Pro-to-Team upgrade and a
        // recovered payment after a decline all pass through no sweep — PastDue disables nothing —
        // and enabling on any of them would overwrite the tenant's own choices, on the dunning path
        // once per billing cycle. A tenant with no prior row has never had rules of its own to keep.
        bool arrivingFromSweptState = (priorSubscription is null) ||
                                      (priorSubscription.Tier == SubscriptionTier.Free) ||
                                      (priorSubscription.Status == SubscriptionStatus.Canceled);

        if (arrivingFromSweptState)
        {
            await _alertRuleRepository.EnableBuiltInAlertRulesAsync(tenantId, ct);
        }

        // Custom rules are Team's in every respect, and a Pro downgrade freezes them without touching
        // their assignments. Arriving at Team from anywhere else thaws them; so does a sweep, which
        // disabled them along with everything else while leaving the tier on Team.
        if (currentTier == SubscriptionTier.Team)
        {
            if ((priorSubscription?.Tier != SubscriptionTier.Team) || arrivingFromSweptState)
            {
                await _alertRuleRepository.EnableCustomAlertRulesAsync(tenantId, ct);
            }
        }
    }
}
