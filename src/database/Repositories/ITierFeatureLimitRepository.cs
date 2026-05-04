// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for managing tier feature limit configurations.
/// </summary>
public interface ITierFeatureLimitRepository
{
    /// <summary>
    /// Gets the feature limits for a specific subscription tier.
    /// </summary>
    Task<TierFeatureLimit?> GetLimitsForTierAsync(SubscriptionTier tier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the feature limits for all tiers.
    /// </summary>
    Task<List<TierFeatureLimit>> GetAllLimitsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the feature limits for a specific subscription tier.
    /// </summary>
    Task UpdateLimitsForTierAsync(SubscriptionTier tier, int? machineLimit, int retentionDays, int? alertRuleLimit, int? webhookLimit, CancellationToken cancellationToken = default);
}
