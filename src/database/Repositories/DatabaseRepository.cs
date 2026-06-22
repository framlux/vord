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
    private readonly IAuditContextAccessor _auditContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseRepository"/> class.
    /// </summary>
    /// <param name="db">The scoped database context.</param>
    /// <param name="logger">Internal structured logger.</param>
    /// <param name="auditContext">
    /// Optional accessor for the client IP address written to audit log entries. Defaults to
    /// <see cref="NullAuditContextAccessor"/>, which leaves the IP unset — appropriate for
    /// background workers and any context without an active HTTP request.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="db"/> or <paramref name="logger"/> is null.</exception>
    public DatabaseRepository(
        DatabaseContext db,
        ILogger<DatabaseRepository> logger,
        IAuditContextAccessor? auditContext = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditContext = auditContext ?? new NullAuditContextAccessor();
    }

    /// <inheritdoc/>
    public async Task<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        LinqToDB.Data.DataConnectionTransaction inner = await _db.BeginTransactionAsync(cancellationToken);

        return new DatabaseTransaction(inner);
    }
}
