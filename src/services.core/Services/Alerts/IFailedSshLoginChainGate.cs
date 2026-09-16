// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// The single-instance lease that decides whether a failed-SSH-login evaluation chain is already
/// running for a machine. Both the ingest path and the chain's own re-arm ask it, so exactly one
/// chain exists per machine no matter how many failures arrive or how long the incident lasts.
/// </summary>
/// <remarks>
/// This replaced a fixed-bucket counter, and the reason is the whole point of the abstraction. A
/// bucket counter answers "did this occurrence open the current bucket", which a trailing chain
/// cannot participate in: every new wall-clock bucket returned "opened" again and started a second
/// chain, while the running chain re-armed itself, so live chains grew by one per window for as long
/// as failures kept arriving. A lease is the same decision asked once by both schedulers.
/// <para>
/// The lease is fenced with a token. A chain that is late enough for its lease to expire can be
/// superseded by a new chain opened at ingest; the superseded run learns that from a refused renewal
/// and stands down rather than releasing a lease it no longer owns.
/// </para>
/// <para>
/// The lease never counts anything anybody alerts on. It is a wake-up and suppression signal; the
/// authority on the count is the telemetry the scheduled evaluation reads back out of Postgres.
/// </para>
/// </remarks>
public interface IFailedSshLoginChainGate
{
    /// <summary>
    /// Attempts to open an evaluation chain for a machine.
    /// </summary>
    /// <param name="machineId">The machine the chain would evaluate.</param>
    /// <returns>
    /// A fencing token when this caller opened the chain and must schedule the first evaluation, or
    /// <c>null</c> when a chain is already live and nothing needs scheduling.
    /// </returns>
    /// <exception cref="StackExchange.Redis.RedisConnectionException">Redis was unreachable.</exception>
    /// <exception cref="StackExchange.Redis.RedisTimeoutException">Redis did not answer in time.</exception>
    Task<string?> TryOpenChainAsync(long machineId);

    /// <summary>
    /// Extends the lease for a chain that is about to schedule its successor.
    /// </summary>
    /// <param name="machineId">The machine the chain is evaluating.</param>
    /// <param name="chainToken">The fencing token this chain was opened with.</param>
    /// <returns>
    /// <c>true</c> when the lease is held (or was free to reclaim) and the successor should be
    /// scheduled; <c>false</c> when another chain has taken over and this one must stand down.
    /// </returns>
    /// <exception cref="StackExchange.Redis.RedisConnectionException">Redis was unreachable.</exception>
    /// <exception cref="StackExchange.Redis.RedisTimeoutException">Redis did not answer in time.</exception>
    Task<bool> RenewChainAsync(long machineId, string chainToken);

    /// <summary>
    /// Releases the lease when a chain ends, so the next failed login can open a new one immediately
    /// rather than waiting out the lease expiry.
    /// </summary>
    /// <param name="machineId">The machine whose chain has ended.</param>
    /// <param name="chainToken">The fencing token this chain was opened with.</param>
    /// <exception cref="StackExchange.Redis.RedisConnectionException">Redis was unreachable.</exception>
    /// <exception cref="StackExchange.Redis.RedisTimeoutException">Redis did not answer in time.</exception>
    Task ReleaseChainAsync(long machineId, string chainToken);
}
