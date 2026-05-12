// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Provides distributed locking via Redis SET NX EX to coordinate background services
/// across multiple Kubernetes replicas.
/// </summary>
public interface IDistributedLock
{
    /// <summary>
    /// Attempts to acquire a distributed lock with the given key.
    /// Returns a disposable handle if the lock was acquired, or null if another instance holds it.
    /// </summary>
    /// <param name="lockKey">The Redis key to use for the lock.</param>
    /// <param name="ttl">How long the lock should be held before auto-expiring.</param>
    /// <returns>A lock handle if acquired, or null if another instance holds the lock.</returns>
    Task<LockHandle?> TryAcquireAsync(string lockKey, TimeSpan ttl);
}
