// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : ITierFeatureLimitRepository
{
    /// <inheritdoc/>
    public async Task<TierFeatureLimit?> GetLimitsForTierAsync(SubscriptionTier tier, CancellationToken cancellationToken)
    {
        TierFeatureLimit? limits = await _db.TierFeatureLimits
            .Where(l => l.Tier == tier)
            .FirstOrDefaultAsync(cancellationToken);

        return limits;
    }

    /// <inheritdoc/>
    public async Task<List<TierFeatureLimit>> GetAllLimitsAsync(CancellationToken cancellationToken)
    {
        List<TierFeatureLimit> limits = await _db.TierFeatureLimits
            .OrderBy(l => l.Tier)
            .ToListAsync(cancellationToken);

        return limits;
    }

    /// <inheritdoc/>
    public async Task UpdateLimitsForTierAsync(SubscriptionTier tier, int? machineLimit, int retentionDays, int? alertRuleLimit, int? webhookLimit, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TierFeatureLimits
            .Where(l => l.Tier == tier)
            .Set(l => l.MachineLimit, machineLimit)
            .Set(l => l.RetentionDays, retentionDays)
            .Set(l => l.AlertRuleLimit, alertRuleLimit)
            .Set(l => l.WebhookLimit, webhookLimit)
            .Set(l => l.UpdatedAt, now)
            .UpdateAsync(cancellationToken);
    }
}
