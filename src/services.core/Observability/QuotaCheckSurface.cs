// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Which surface asked for a quota pause decision that could not be made. Each surface fails in its
/// own way, so the failure counter is split by it rather than reported as one total.
/// </summary>
public enum QuotaCheckSurface
{
    /// <summary>A telemetry batch being accepted.</summary>
    Ingest,

    /// <summary>An agent heartbeat.</summary>
    Ping,

    /// <summary>Delivery of remote commands to an agent.</summary>
    Commands,

    /// <summary>A machine's paused state being shown in the API or the UI.</summary>
    Display,

    /// <summary>Alert rule evaluation for a tenant.</summary>
    AlertEvaluation,
}
