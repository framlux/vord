// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Canonical lock-name constants and prefixes used by
/// <see cref="IAdvisoryLockProvider.TryAcquireAsync"/> callers. Centralized so the entire set
/// of lock-name domains is visible at one site — necessary context for the SHA-256
/// 64-bit-truncation collision analysis documented on
/// <see cref="PostgresAdvisoryLockProvider"/>.
/// </summary>
/// <remarks>
/// Collision probability follows the birthday bound: with N distinct hashed names in flight,
/// expected first collision around N ≈ 2^32 (~4 billion). All currently-used lock domains are
/// either fixed strings or tenant-scoped (<see cref="HealthSweepTenantPrefix"/>), bounded by
/// tenant count. Adding a new lock-name domain with unbounded cardinality (e.g., per-request
/// keys) requires re-evaluating this design — consider a per-domain numeric namespace prefix
/// rather than a string concatenation.
/// </remarks>
public static class LockNames
{
    /// <summary>State-streaming singleton lock (one consumer per cluster).</summary>
    public const string StateStreaming = "state-streaming";

    /// <summary>Usage-heartbeat singleton lock (one heartbeat run per cluster per hour).</summary>
    public const string UsageHeartbeat = "usage-heartbeat";

    /// <summary>
    /// Prefix for the per-tenant health-sweep lock; the full key is
    /// <c>health-sweep:tenant:&lt;tenantId&gt;</c>.
    /// </summary>
    public const string HealthSweepTenantPrefix = "health-sweep:tenant:";

    /// <summary>Every fixed (non-prefixed) lock name. Used by the collision-audit test.</summary>
    public static IReadOnlyList<string> FixedNames { get; } = new[]
    {
        StateStreaming,
        UsageHeartbeat,
    };

    /// <summary>Every prefix used to compose tenant-scoped lock names.</summary>
    public static IReadOnlyList<string> Prefixes { get; } = new[]
    {
        HealthSweepTenantPrefix,
    };
}
