// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Configuration options for Redis connection.
/// </summary>
public sealed class RedisOptions
{
    /// <summary>
    /// The Redis connection string.
    /// </summary>
    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// How long, in seconds, a tenant's subscription status is cached in Redis on the hot path.
    /// Kept short so staleness is bounded; the cache is also invalidated immediately on any
    /// subscription mutation. Defaults to 30 seconds.
    /// </summary>
    public int SubscriptionCacheTtlSeconds { get; set; } = 30;
}
