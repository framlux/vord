// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for the ASP.NET Core Data Protection key ring, persisted in Postgres so
/// api-server and services-worker replicas share the same ring.
/// </summary>
public interface IDataProtectionKeyRepository
{
    /// <summary>
    /// Returns every key ring entry. The ring is tiny (dozens of rows accumulated over years),
    /// so this is always a full table read.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>All key ring entries currently persisted.</returns>
    Task<IReadOnlyList<DataProtectionKey>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new key ring entry.
    /// </summary>
    /// <param name="key">The entry to insert.</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns an awaitable Task</returns>
    Task InsertAsync(DataProtectionKey key, CancellationToken cancellationToken = default);
}
