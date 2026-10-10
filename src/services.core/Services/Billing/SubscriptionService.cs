// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Service for managing tenant subscriptions and billing.
/// </summary>
public sealed class SubscriptionService : ISubscriptionService
{
    private readonly ISubscriptionRepository _subscriptionRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IIntegrationRepository _integrationRepo;
    private readonly ITierFeatureLimitRepository _tierLimitRepo;
    private readonly ITenantSubscriptionOverrideRepository _overrideRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IInvitationRepository _invitationRepo;
    private readonly TierDefaultOptions _tierDefaults;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubscriptionService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="SubscriptionService"/> class.
    /// </summary>
    public SubscriptionService(
        ISubscriptionRepository subscriptionRepo,
        IMachineRepository machineRepo,
        IAlertRuleRepository alertRuleRepo,
        IIntegrationRepository integrationRepo,
        ITierFeatureLimitRepository tierLimitRepo,
        ITenantSubscriptionOverrideRepository overrideRepo,
        ITenantRepository tenantRepo,
        IInvitationRepository invitationRepo,
        IOptions<TierDefaultOptions> tierDefaults,
        TimeProvider timeProvider,
        ILogger<SubscriptionService> logger)
    {
        ArgumentNullException.ThrowIfNull(subscriptionRepo);
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(alertRuleRepo);
        ArgumentNullException.ThrowIfNull(integrationRepo);
        ArgumentNullException.ThrowIfNull(tierLimitRepo);
        ArgumentNullException.ThrowIfNull(overrideRepo);
        ArgumentNullException.ThrowIfNull(tenantRepo);
        ArgumentNullException.ThrowIfNull(invitationRepo);
        ArgumentNullException.ThrowIfNull(tierDefaults);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _subscriptionRepo = subscriptionRepo;
        _machineRepo = machineRepo;
        _alertRuleRepo = alertRuleRepo;
        _integrationRepo = integrationRepo;
        _tierLimitRepo = tierLimitRepo;
        _overrideRepo = overrideRepo;
        _tenantRepo = tenantRepo;
        _invitationRepo = invitationRepo;
        _tierDefaults = tierDefaults.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        return subscription;
    }

    /// <summary>
    /// Ingest eligibility: an active-or-past-due subscription AND an active tenant. A deactivated
    /// tenant (a pending deletion) never ingests, even on a Free/Active subscription.
    /// </summary>
    internal static bool IsIngestEligible(TenantSubscription? subscription, bool tenantIsActive)
    {
        if (tenantIsActive == false)
        {
            return false;
        }

        // Ingest policy lives here so it has a single home. PastDue is treated as a grace period —
        // ingest continues during Stripe dunning, matching the web app's "PastDue keeps access" behavior.
        // Dunning ends in either recovery (Active) or customer.subscription.deleted, which deactivates and
        // stops ingest. Canceled and no-subscription are not eligible.
        return (subscription is not null) &&
               ((subscription.Status == SubscriptionStatus.Active) || (subscription.Status == SubscriptionStatus.PastDue));
    }

    /// <inheritdoc/>
    public async Task<bool> IsIngestEligibleAsync(int tenantId, CancellationToken ct)
    {
        Tenant? tenant = await _tenantRepo.GetTenantByIdAsync(tenantId, ct);
        if ((tenant is null) || (tenant.IsActive == false))
        {
            return false;
        }

        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        return IsIngestEligible(subscription, tenant.IsActive);
    }

    /// <inheritdoc/>
    public async Task<int> GetRetentionDaysForTenantAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return 1;
        }

        TenantSubscriptionOverride? tenantOverride = await _overrideRepo.GetOverrideForTenantAsync(tenantId, ct);
        if (tenantOverride?.RetentionDays is not null)
        {
            return tenantOverride.RetentionDays.Value;
        }

        TierFeatureLimit? tierLimits = await _tierLimitRepo.GetLimitsForTierAsync(subscription.Tier, ct);

        return tierLimits?.RetentionDays ?? 1;
    }

    /// <inheritdoc/>
    public async Task<int> GetEffectiveRetentionDaysForTenantAsync(int tenantId, CancellationToken ct)
    {
        return await _subscriptionRepo.GetEffectiveRetentionDaysAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public async Task<int> GetMachineCountForTenantAsync(int tenantId, CancellationToken ct)
    {
        int count = await _machineRepo.GetActiveMachineCountAsync(tenantId, ct);

        return count;
    }

    /// <summary>
    /// Floors used when a TierFeatureLimits row is missing. Falling back to zero would bill a
    /// paid subscription nothing, so the known values are duplicated here deliberately.
    /// </summary>
    private static readonly Dictionary<SubscriptionTier, int> FallbackFloors = new()
    {
        [SubscriptionTier.Free] = 0,
        [SubscriptionTier.Pro] = 1,
        [SubscriptionTier.Team] = 3,
        // Enterprise is never billed through Stripe; it has no floor.
        [SubscriptionTier.Enterprise] = 0,
    };

    /// <inheritdoc/>
    public async Task<int> GetBillableMachineCountAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        // Only the tiers billed through Stripe have a billable quantity. None has no floor policy,
        // and Enterprise is invoiced outside Stripe, so a caller passing either (e.g. a subscription
        // row that predates a tier being set) must not silently fall through to a count that could
        // be pushed to Stripe. Refuse loudly instead of encoding the silent-zero into
        // FallbackFloors, which would make it look like intended behaviour. Every caller of this
        // method already treats per-tenant billing as best-effort and catches around it, so
        // throwing here surfaces as a loud per-tenant log line rather than an outage.
        if (StripeBilledTiers.Contains(tier) == false)
        {
            _logger.LogError(
                "GetBillableMachineCountAsync called with non-Stripe-billed tier {Tier} for tenant {TenantId}; refusing to compute a billable quantity",
                tier, tenantId);

            throw new InvalidOperationException(
                $"Tenant {tenantId} tier {tier} is not billed through Stripe; cannot compute a billable machine count");
        }

        int active = await _machineRepo.GetActiveMachineCountAsync(tenantId, ct);

        TierFeatureLimit? tierLimits = await _tierLimitRepo.GetLimitsForTierAsync(tier, ct);

        int floor;
        if (tierLimits is null)
        {
            floor = FallbackFloors.TryGetValue(tier, out int fallback) ? fallback : 0;
            _logger.LogWarning(
                "No TierFeatureLimits found for tier {Tier}, using fallback billable floor {Floor}",
                tier, floor);
        }
        else
        {
            floor = tierLimits.MinimumBillableMachines;
        }

        // A host beyond the tier's limit is paused and not monitored, so it is not billed either. The
        // limit is the one for the tier being billed, which at checkout is the tier being bought.
        int limit = await GetMachineLimitForTierAsync(tenantId, tier, ct);
        int billed = Math.Min(active, Math.Max(0, limit));

        return Math.Max(billed, floor);
    }

    /// <summary>
    /// The one fold every machine limit goes through, so billing, pausing and the limits shown to the
    /// customer cannot disagree about which value wins.
    /// </summary>
    internal static int ResolveLimit(int? overrideValue, int? tierRowValue, int configDefault)
    {
        return overrideValue ?? tierRowValue ?? configDefault;
    }

    /// <inheritdoc/>
    public async Task<int> GetMachineLimitForTierAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        TenantSubscriptionOverride? tenantOverride = await _overrideRepo.GetOverrideForTenantAsync(tenantId, ct);
        TierFeatureLimit? tierLimits = await _tierLimitRepo.GetLimitsForTierAsync(tier, ct);

        return ResolveLimit(tenantOverride?.MachineLimit, tierLimits?.MachineLimit, GetConfigDefaultsForTier(tier).MachineLimit);
    }

    /// <inheritdoc/>
    public async Task<bool> CanCreateAlertRuleAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return false;
        }

        EffectiveLimits limits = await GetEffectiveLimitsForTenantAsync(tenantId, ct);
        // Only custom rules count against the allowance. Built-ins ship with the product and are
        // provisioned for every tenant, so counting them would charge a tenant for rules it never
        // authored — and would put a Free tenant permanently over a limit of zero.
        int count = await _alertRuleRepo.CountCustomAlertRulesForTenantAsync(tenantId, ct);

        return count < limits.AlertRuleLimit;
    }

    /// <inheritdoc/>
    public async Task<bool> CanCreateWebhookAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return false;
        }

        EffectiveLimits limits = await GetEffectiveLimitsForTenantAsync(tenantId, ct);
        int count = await _integrationRepo.CountIntegrationsForTenantAsync(tenantId, ct);

        return count < limits.WebhookLimit;
    }

    /// <inheritdoc/>
    public async Task<bool> CanAddMemberAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return false;
        }

        EffectiveLimits limits = await GetEffectiveLimitsForTenantAsync(tenantId, ct);
        int activeMembers = await _tenantRepo.CountActiveMembersAsync(tenantId, ct);
        int pendingInvitations = await _invitationRepo.CountPendingInvitationsAsync(tenantId, _timeProvider.GetUtcNow(), ct);

        return (activeMembers + pendingInvitations) < limits.MemberLimit;
    }

    /// <inheritdoc/>
    public async Task<int> GetMachineCountAtDateAsync(int tenantId, DateTimeOffset targetDate, CancellationToken ct)
    {
        int count = await _machineRepo.GetMachineCountAtDateAsync(tenantId, targetDate, ct);

        return count;
    }

    /// <inheritdoc/>
    public async Task<EffectiveLimits> GetEffectiveLimitsForTenantAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return ResolveEffectiveLimits(null, null, null, _tierDefaults);
        }

        TierFeatureLimit? tierLimits = await _tierLimitRepo.GetLimitsForTierAsync(subscription.Tier, ct);
        TenantSubscriptionOverride? tenantOverride = await _overrideRepo.GetOverrideForTenantAsync(tenantId, ct);

        if (tierLimits is null)
        {
            // Fallback to configuration defaults when the database row is missing
            _logger.LogWarning("No TierFeatureLimits found for tier {Tier}, using configuration defaults", subscription.Tier);
        }

        return ResolveEffectiveLimits(subscription, tierLimits, tenantOverride, _tierDefaults);
    }

    /// <summary>
    /// The one fold for a tenant's effective limits, shared by the subscription service, the pause
    /// decision and the admin contract so they can never disagree about which value wins. A tenant
    /// with no subscription row gets the Free defaults and its override is ignored, matching how
    /// every other entitlement treats a missing row.
    /// </summary>
    /// <param name="subscription">The tenant's subscription row, or null when it has none.</param>
    /// <param name="tierRow">The live limits row for the subscription's tier, or null when it is missing.</param>
    /// <param name="tenantOverride">The tenant's per-limit overrides, or null when it has none.</param>
    /// <param name="defaults">The configured per-tier defaults, used for any limit neither the override nor the tier row sets.</param>
    /// <returns>The limits that apply to the tenant.</returns>
    public static EffectiveLimits ResolveEffectiveLimits(
        TenantSubscription? subscription,
        TierFeatureLimit? tierRow,
        TenantSubscriptionOverride? tenantOverride,
        TierDefaultOptions defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        if (subscription is null)
        {
            TierLimitDefaults free = defaults.For(SubscriptionTier.Free);

            return new EffectiveLimits
            {
                MachineLimit = free.MachineLimit,
                RetentionDays = free.RetentionDays,
                AlertRuleLimit = free.AlertRuleLimit,
                WebhookLimit = free.WebhookLimit,
                MemberLimit = free.MemberLimit,
            };
        }

        TierLimitDefaults tierDefaults = defaults.For(subscription.Tier);

        return new EffectiveLimits
        {
            MachineLimit = ResolveLimit(tenantOverride?.MachineLimit, tierRow?.MachineLimit, tierDefaults.MachineLimit),
            RetentionDays = tenantOverride?.RetentionDays ?? tierRow?.RetentionDays ?? tierDefaults.RetentionDays,
            AlertRuleLimit = tenantOverride?.AlertRuleLimit ?? tierRow?.AlertRuleLimit ?? tierDefaults.AlertRuleLimit,
            WebhookLimit = tenantOverride?.WebhookLimit ?? tierRow?.WebhookLimit ?? tierDefaults.WebhookLimit,
            MemberLimit = tenantOverride?.MemberLimit ?? tierRow?.MemberLimit ?? tierDefaults.MemberLimit,
        };
    }

    /// <inheritdoc/>
    public async Task<TimeSpan> GetDataExportCooldownAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        // A missing subscription resolves to the free window, the same way every other effective
        // limit resolves it. Answering "no window" here would make the absence of a row the
        // cheapest way to export without limit.
        SubscriptionTier tier = subscription?.Tier ?? SubscriptionTier.Free;

        return TimeSpan.FromHours(GetConfigDefaultsForTier(tier).DataExportCooldownHours);
    }

    /// <summary>
    /// Gets the configuration-driven default limits for a subscription tier.
    /// </summary>
    private TierLimitDefaults GetConfigDefaultsForTier(SubscriptionTier tier)
    {
        return _tierDefaults.For(tier);
    }
}
