// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Data;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Wraps a LinqToDB <see cref="DataConnectionTransaction"/> behind the
/// <see cref="IDatabaseTransaction"/> interface for testability.
/// </summary>
internal sealed class DatabaseTransaction : IDatabaseTransaction
{
    private readonly DataConnectionTransaction _inner;

    /// <summary>
    /// Creates a new wrapper around the given transaction.
    /// </summary>
    /// <param name="inner">The LinqToDB transaction to wrap.</param>
    public DatabaseTransaction(DataConnectionTransaction inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc/>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await _inner.CommitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _inner.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
    }
}
