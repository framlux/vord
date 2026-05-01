// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Abstracts a database transaction for testability. Wraps the underlying
/// LinqToDB DataConnectionTransaction so that callers do not depend on concrete types.
/// </summary>
public interface IDatabaseTransaction : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Commits the transaction.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task CommitAsync(CancellationToken cancellationToken = default);
}
