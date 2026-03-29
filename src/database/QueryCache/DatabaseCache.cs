// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Cache;

/// <inheritdoc/>
public partial class DatabaseCache : IDatabaseCache
{
    private readonly DatabaseContext _db;
    private readonly ILogger<DatabaseCache> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseCache"/> class.
    /// </summary>
    /// <param name="db">The scoped database context.</param>
    /// <param name="logger">Internal structured logger.</param>
    /// <exception cref="ArgumentNullException"></exception>
    public DatabaseCache(DatabaseContext db, ILogger<DatabaseCache> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<DataConnectionTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        return await _db.BeginTransactionAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task InsertAuditLogAsync(AuditLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _db.InsertAsync(entry, token: cancellationToken);
    }
}
