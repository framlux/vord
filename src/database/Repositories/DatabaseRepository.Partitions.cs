// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IPartitionRepository
{
    /// <inheritdoc/>
    public async Task<int> GetLongClassRetentionDaysAsync(CancellationToken cancellationToken)
    {
        // The Long window is the greater of the 365-day floor and the largest retention override
        // across all tenants, so a rare over-365-day override extends only the Long class.
        int? maxOverrideRetention = await _db.TenantSubscriptionOverrides
            .MaxAsync(o => o.RetentionDays, cancellationToken);

        return Math.Max(RetentionClassPolicy.LongWindowDays, maxOverrideRetention ?? 0);
    }

    /// <inheritdoc/>
    public async Task ExecutePartitionDdlAsync(string sql, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await _db.ExecuteAsync(sql, cancellationToken);
    }
}
