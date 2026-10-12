// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database;

/// <summary>
/// The time windows that decide when a cluster's reporting state is out of date. Declared next to the
/// repository so garbage collection and every reader of a cluster's reporting state share one value.
/// </summary>
public static class KubernetesReportingPolicy
{
    /// <summary>
    /// How long a cluster may go without an accepted snapshot before it counts as stale. The holder
    /// sends at least every 30 minutes, so this leaves room for three missed sends.
    /// </summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(90);
}
