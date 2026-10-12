// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// How much of a cluster Vord can currently see through the cluster reporter. The states are listed in
/// the order <see cref="KubernetesReporting.Derive"/> tests them.
/// </summary>
public enum ReportingState
{
    /// <summary>No machine is enrolled as a reporter candidate for the cluster.</summary>
    Off,

    /// <summary>A reporter cannot use its credentials or reach the cluster.</summary>
    Error,

    /// <summary>The permissions installed in the cluster are older than the agent expects.</summary>
    RoleOutdated,

    /// <summary>Reporters are enrolled but no snapshot has arrived recently.</summary>
    Stale,

    /// <summary>Reporters are enrolled and a recent snapshot has arrived.</summary>
    Active,
}
