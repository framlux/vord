// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// Configuration options for Hangfire integration, bound from the "Hangfire" configuration section.
/// </summary>
public sealed class HangfireOptions
{
    /// <summary>The Postgres schema name used by Hangfire. Defaults to "hangfire".</summary>
    public string SchemaName { get; set; } = "hangfire";

    /// <summary>Worker count for the Hangfire server. Defaults to 10.</summary>
    public int WorkerCount { get; set; } = 10;

    /// <summary>Whether the Hangfire dashboard is mounted by the server process. Defaults to true.</summary>
    public bool DashboardEnabled { get; set; } = true;
}
