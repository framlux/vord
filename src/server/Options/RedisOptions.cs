// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Server.Options;

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
}
