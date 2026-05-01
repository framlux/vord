// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

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
}
