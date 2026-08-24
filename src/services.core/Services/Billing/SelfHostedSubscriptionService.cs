// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Entitlement view for a self-hosted deployment, where there are no subscription tiers and
/// nothing is gated. Wraps the real subscription service and answers every entitlement question
/// permissively while delegating the questions that carry no entitlement meaning.
/// </summary>
/// <remarks>
/// <para>
/// The tenant's stored subscription row remains Free — this type does not write anything. It
/// reports a synthetic Team, Active subscription so the tier checks scattered across the endpoints
/// pass without each having to learn about deployment mode.
/// </para>
/// <para>
/// That synthetic tier does reach a write path: machine registration takes a Pro-or-Team branch
/// that calls IBillingApiClient.UpdateQuantityAsync. It is harmless only because a self-hosted
/// deployment resolves NoOpBillingApiClient, which does nothing and reports success. That no-op is
/// the invariant keeping this safe — not any routing rule — so any future write keyed on tier must
/// be checked against it.
/// </para>
/// <para>
/// One path deliberately escapes this decorator: RetentionReclassifyJob injects an uncached,
/// undecorated ISubscriptionRepository and would see the real Free row. It is dormant in
/// self-hosted because its only dispatch site is the SaaS-only FleetAdminService, and making it
/// reachable here would require revisiting this class.
/// </para>
/// <para>
/// Every member must be implemented explicitly. A member left delegating silently reintroduces a
/// Free-tier limit that the user interface will not reflect and no error will explain.
/// </para>
/// </remarks>
public sealed class SelfHostedSubscriptionService : ISubscriptionService
{
    private readonly ISubscriptionService _inner;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="SelfHostedSubscriptionService"/> class.
    /// </summary>
    /// <param name="inner">The real subscription service, used for non-entitlement queries.</param>
    /// <param name="timeProvider">
    /// Clock used to stamp the synthetic subscription. The synthetic row is serialized by the
    /// subscription endpoint, so its timestamps are user-visible and must be a real time.
    /// </param>
    public SelfHostedSubscriptionService(ISubscriptionService inner, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _inner = inner;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken ct = default)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        TenantSubscription synthetic = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Team,
            Status = SubscriptionStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        return Task.FromResult<TenantSubscription?>(synthetic);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Deliberately delegated. This is not an entitlement question: the real implementation checks
    /// the tenant's active flag first, which is how tenant deactivation and pending deletion stop
    /// telemetry within a single request. Answering permissively here would let a deactivated
    /// tenant ingest forever. A live self-hosted tenant is eligible anyway, because eligibility
    /// accepts any active subscription regardless of tier.
    /// </remarks>
    public Task<bool> IsIngestEligibleAsync(int tenantId, CancellationToken ct = default)
    {
        return _inner.IsIngestEligibleAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public Task<TenantSubscription> ProvisionFreeSubscriptionAsync(int tenantId, CancellationToken ct = default)
    {
        return _inner.ProvisionFreeSubscriptionAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the widest retention class the partitioning scheme supports rather than a nominally
    /// unlimited value. There is no unlimited class; anything above the long window would be
    /// classified as Long anyway, so reporting Long keeps the stated retention honest.
    /// </remarks>
    public Task<int> GetRetentionDaysForTenantAsync(int tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(RetentionClassPolicy.LongWindowDays);
    }

    /// <inheritdoc/>
    public Task<int> GetEffectiveRetentionDaysForTenantAsync(int tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(RetentionClassPolicy.LongWindowDays);
    }

    /// <inheritdoc/>
    public Task<int> GetMachineCountForTenantAsync(int tenantId, CancellationToken ct = default)
    {
        return _inner.GetMachineCountForTenantAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public Task EnsureSubscriptionExistsAsync(int tenantId, CancellationToken ct = default)
    {
        return _inner.EnsureSubscriptionExistsAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public Task<int> GetMachineCountAtDateAsync(int tenantId, DateTimeOffset targetDate, CancellationToken ct = default)
    {
        return _inner.GetMachineCountAtDateAsync(tenantId, targetDate, ct);
    }

    /// <inheritdoc/>
    public Task<bool> CanCreateAlertRuleAsync(int tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> CanCreateWebhookAsync(int tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<EffectiveLimits> GetEffectiveLimitsForTenantAsync(int tenantId, CancellationToken ct = default)
    {
        EffectiveLimits limits = new()
        {
            MachineLimit = int.MaxValue,
            RetentionDays = RetentionClassPolicy.LongWindowDays,
            AlertRuleLimit = int.MaxValue,
            WebhookLimit = int.MaxValue,
            MemberLimit = int.MaxValue,
        };

        return Task.FromResult(limits);
    }

    /// <inheritdoc/>
    public Task<bool> CanAddMemberAsync(int tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<int> GetBillableMachineCountAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        return _inner.GetBillableMachineCountAsync(tenantId, tier, ct);
    }
}
