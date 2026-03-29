// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;

/// <summary>
/// Machine health status levels.
/// </summary>
public enum MachineHealthStatus
{
    /// <summary>All metrics nominal.</summary>
    Healthy = 0,

    /// <summary>One or more metrics elevated.</summary>
    Warning = 1,

    /// <summary>One or more metrics in critical range.</summary>
    Critical = 2,

    /// <summary>Machine not responding to pings.</summary>
    Offline = 3
}
