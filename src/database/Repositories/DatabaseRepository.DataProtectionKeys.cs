// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IDataProtectionKeyRepository
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<DataProtectionKey>> GetAllAsync(CancellationToken cancellationToken)
    {
        List<DataProtectionKey> keys = await _db.DataProtectionKeys.ToListAsync(cancellationToken);

        return keys;
    }

    /// <inheritdoc/>
    public async Task InsertAsync(DataProtectionKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _db.InsertAsync(key, token: cancellationToken);
    }
}
