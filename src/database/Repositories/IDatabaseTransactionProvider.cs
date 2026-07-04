// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Data;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Provides database transaction management.
/// </summary>
public interface IDatabaseTransactionProvider
{
    /// <summary>
    /// Begins a database transaction on the shared scoped connection.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a transaction that must be committed or disposed</returns>
    Task<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins a database transaction on the shared scoped connection at the requested isolation level.
    /// Use <see cref="IsolationLevel.Serializable"/> together with <see cref="IsSerializationConflict"/>
    /// and a bounded retry loop for read-then-write invariants that must not race across connections.
    /// </summary>
    /// <param name="isolationLevel">The isolation level for the transaction.</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a transaction that must be committed or disposed</returns>
    Task<IDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true when the exception is a serialization/isolation conflict (Postgres SQLSTATE 40001)
    /// that a Serializable-isolation caller should retry against fresh state.
    /// </summary>
    /// <param name="exception">The exception thrown by the database driver.</param>
    /// <returns><c>true</c> when the exception represents a retryable serialization conflict.</returns>
    bool IsSerializationConflict(Exception exception);
}
