// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IDatabaseTransactionProvider
{
    private readonly DatabaseContext _db;
    private readonly ILogger<DatabaseRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseRepository"/> class.
    /// </summary>
    /// <param name="db">The scoped database context.</param>
    /// <param name="logger">Internal structured logger.</param>
    /// <exception cref="ArgumentNullException"></exception>
    public DatabaseRepository(DatabaseContext db, ILogger<DatabaseRepository> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        LinqToDB.Data.DataConnectionTransaction inner = await _db.BeginTransactionAsync(cancellationToken);

        return new DatabaseTransaction(inner);
    }
}
