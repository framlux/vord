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
    public async Task<int?> GetMaxRetentionDaysAsync(CancellationToken cancellationToken)
    {
        return await _db.TierFeatureLimits
            .MaxAsync(l => (int?)l.RetentionDays, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ExecutePartitionDdlAsync(string sql, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await _db.ExecuteAsync(sql, cancellationToken);
    }
}
