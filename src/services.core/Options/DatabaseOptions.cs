// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Configuration options for PostgreSQL database connection.
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>
    /// The database server hostname.
    /// </summary>
    [Required]
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// The database user name.
    /// </summary>
    [Required]
    public string User { get; set; } = string.Empty;

    /// <summary>
    /// The database password.
    /// </summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The database name.
    /// </summary>
    [Required]
    public string Db { get; set; } = string.Empty;

    /// <summary>
    /// Maximum Npgsql connection-pool size. Defaults to 50. Size the worker higher than the
    /// api-server when the projection/alert jobs need more concurrent connections.
    /// </summary>
    public int MaxPoolSize { get; set; } = 50;

    /// <summary>
    /// Minimum Npgsql connection-pool size kept warm. Defaults to 5.
    /// </summary>
    public int MinPoolSize { get; set; } = 5;
}
