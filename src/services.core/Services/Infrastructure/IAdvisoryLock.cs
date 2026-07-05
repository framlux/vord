// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// A held advisory lock. Disposing releases it. <see cref="IsAliveAsync"/> lets a long-running holder
/// verify the underlying lock session is still connected, so it can detect a silent lock loss (failover,
/// network partition, or an <c>idle_in_transaction_session_timeout</c>) and stop acting as the owner.
/// </summary>
public interface IAdvisoryLock : IAsyncDisposable
{
    /// <summary>
    /// Returns <see langword="true"/> if the lock session is still alive and holding the lock. Any
    /// failure to round-trip a trivial query on the lock's own connection is treated as a lost lock and
    /// returns <see langword="false"/>. Running this also keeps the lock session non-idle, which
    /// neutralizes an <c>idle_in_transaction_session_timeout</c> configured on the role.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the lock is verified alive; otherwise <see langword="false"/>.</returns>
    Task<bool> IsAliveAsync(CancellationToken ct);
}
