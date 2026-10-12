// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Kubernetes;

/// <summary>
/// The Kubernetes view of one machine: what it reported locally, the node it is linked to, and the
/// cluster it belongs to.
/// </summary>
public sealed class MachineKubernetesDto
{
    /// <summary>The machine's latest local report from the past seven days, or null when there is none.</summary>
    public MachineKubernetesLocalDto? Local { get; set; }

    /// <summary>The node the machine is linked to, or null when it is not a known node.</summary>
    public KubernetesNodeDto? Node { get; set; }

    /// <summary>The ID of the cluster the machine belongs to, or null when it has none or the choice is ambiguous.</summary>
    public long? ClusterId { get; set; }

    /// <summary>The name of the cluster in <see cref="ClusterId"/>.</summary>
    public string? ClusterName { get; set; }

    /// <summary>How the machine relates to its tenant's clusters; one of the <see cref="KubernetesGrouping"/> values.</summary>
    public string Grouping { get; set; } = KubernetesGrouping.Ungrouped;
}
