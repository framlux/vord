// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Provides named advisory locks coordinated across all server replicas via the shared database.
/// Replaces the former Redis-backed IDistributedLock for jobs whose serialization key is known per
/// invocation (e.g., per-tenant health sweep).
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL transaction-scoped advisory locks (pg_try_advisory_xact_lock) auto-release on
/// connection close. The implementation pins a dedicated NpgsqlConnection for the lifetime of
/// the lock handle, so connection-pool reuse does NOT release the lock on a different backend.
/// </para>
/// <para>
/// Crash-recovery: if the worker process is OOM-killed or SIGKILLed, the held lock is released
/// only when Postgres detects the dead TCP connection. With default Linux settings that can
/// take minutes-to-hours (tcp_keepalive_time defaults to 7200 seconds). The connection string
/// used by the provider sets Keepalive=30 (and TcpKeepAlive=true) so a stale connection is
/// detected within roughly one minute (probes at 30s intervals).
/// </para>
/// <para>
/// Expected hold-window per job: locks are taken at the start of a Hangfire job invocation and
/// released when the job's <see cref="IAsyncDisposable"/> handle is disposed (typically tens of
/// seconds to a few minutes for per-tenant health sweeps and similar serialization keys). Jobs
/// that would hold a lock for longer should be split or use a different coordination primitive.
/// </para>
/// </remarks>
public interface IAdvisoryLockProvider
{
    /// <summary>
    /// Tries to acquire an exclusive advisory lock identified by <paramref name="lockName"/>.
    /// Returns an <see cref="IAsyncDisposable"/> that releases the lock when disposed, or
    /// <see langword="null"/> if another holder already owns the lock.
    /// </summary>
    /// <param name="lockName">Unique identifier for the lock (caller composes per-resource keys).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A disposable holding the lock, or <see langword="null"/> if not acquired.</returns>
    Task<IAsyncDisposable?> TryAcquireAsync(string lockName, CancellationToken ct);
}
