// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Service for managing tenant subscriptions and billing.
/// </summary>
public interface ISubscriptionService
{
    /// <summary>
    /// Gets the subscription for a tenant.
    /// </summary>
    Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// The single source of truth for whether a tenant's machines may ingest telemetry right now. The
    /// telemetry paths (unary and the mid-stream recheck) call this so ingest policy lives in one place.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the ingesting machine.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when telemetry ingest is allowed for the tenant.</returns>
    Task<bool> IsIngestEligibleAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Gets the retention days for a tenant.
    /// </summary>
    Task<int> GetRetentionDaysForTenantAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Gets the tenant's effective retention days from the cached subscription entry. Unlike
    /// <see cref="GetRetentionDaysForTenantAsync"/>, this resolves through the short-TTL subscription
    /// cache so the telemetry ingest hot path can stamp a row's retention class without a per-envelope
    /// database round-trip.
    /// </summary>
    Task<int> GetEffectiveRetentionDaysForTenantAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Gets the active machine count for a tenant.
    /// </summary>
    Task<int> GetMachineCountForTenantAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Gets the machine count for a tenant at a specific point in time.
    /// Reconstructs from RegisteredOn/DeletedOn timestamps.
    /// </summary>
    Task<int> GetMachineCountAtDateAsync(int tenantId, DateTimeOffset targetDate, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the tenant can create another alert rule within their subscription limit.
    /// </summary>
    /// <param name="tenantId">The tenant to check.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> CanCreateAlertRuleAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the tenant can create another webhook endpoint within their subscription limit.
    /// </summary>
    /// <param name="tenantId">The tenant to check.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> CanCreateWebhookAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Gets the effective feature limits for a tenant, considering tier defaults and per-tenant overrides.
    /// </summary>
    Task<EffectiveLimits> GetEffectiveLimitsForTenantAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Gets how long the tenant must wait between generating data exports. Governs generating an
    /// export only — downloading an export that already exists is not rationed. A tenant with no
    /// subscription resolves to the free window rather than to no window.
    /// </summary>
    /// <param name="tenantId">The tenant to check.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TimeSpan> GetDataExportCooldownAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the tenant can add another member within their tier limit. The limit is enforced
    /// against active members plus pending non-expired invitations so a tenant cannot over-invite past the cap.
    /// </summary>
    /// <param name="tenantId">The tenant to check.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> CanAddMemberAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the machine count Stripe should be billed for: the tenant's active machine
    /// count raised to the floor for <paramref name="tier"/>.
    /// </summary>
    /// <param name="tenantId">The tenant to count machines for.</param>
    /// <param name="tier">The tier whose floor applies. Callers pass the tier being billed,
    /// which at checkout is the tier being purchased rather than the tenant's current one.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> GetBillableMachineCountAsync(int tenantId, SubscriptionTier tier, CancellationToken ct);
}
