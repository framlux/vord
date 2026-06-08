// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// Configuration options for Hangfire integration, bound from the "Hangfire" configuration section.
/// </summary>
public sealed class HangfireOptions
{
    /// <summary>The Postgres schema name used by Hangfire. Defaults to "hangfire".</summary>
    [Required]
    [MinLength(1)]
    public string SchemaName { get; set; } = "hangfire";

    /// <summary>Worker count for the Hangfire server. Defaults to 10.</summary>
    [Range(1, 200)]
    public int WorkerCount { get; set; } = 10;

    /// <summary>Whether the Hangfire dashboard is mounted by the server process. Defaults to true.</summary>
    public bool DashboardEnabled { get; set; } = true;

    /// <summary>
    /// How long (in minutes) a job can remain invisible (in-flight) before Hangfire considers
    /// it abandoned and makes it visible for redelivery. Defaults to 120 minutes (2 hours).
    /// Tune lower during incident response to force re-delivery faster; tune higher when
    /// legitimate long-running jobs exceed the default.
    /// </summary>
    [Range(1, 1440)]
    public int InvisibilityTimeoutMinutes { get; set; } = 120;
}
