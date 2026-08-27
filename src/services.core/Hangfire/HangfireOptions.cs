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

    /// <summary>
    /// How long (in seconds) after an operator runs a recurring job on demand before the same job
    /// may be run on demand again. Defaults to 60.
    /// </summary>
    /// <remarks>
    /// This exists to bound worker starvation, not to police intent. Every recurring job carries
    /// <c>[DisableConcurrentExecution]</c>, whose filter blocks a worker thread for up to the job's
    /// timeout — 1800 seconds for partition management and data-export processing — against a
    /// default worker count of 10 on a single node. Without a cooldown, a handful of impatient
    /// clicks during an incident parks most of the pool and starves the per-minute jobs on the
    /// critical queue, which is a background-processing outage caused by a button. A short cooldown
    /// defeats that accident; it does not stop an operator who deliberately spaces runs out, which
    /// is a considered choice rather than a slip.
    /// </remarks>
    public int ManualRunCooldownSeconds { get; set; } = 60;
}
