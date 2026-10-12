// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Kubernetes;

/// <summary>
/// What a machine itself reported about the Kubernetes it runs. The CA fingerprint it also reports is
/// used to group machines into clusters and is deliberately not part of this view.
/// </summary>
public sealed class MachineKubernetesLocalDto
{
    /// <summary>The Kubernetes distribution detected on the host.</summary>
    public string Distro { get; set; } = "";

    /// <summary>The kubelet version found on the host, empty when none was found.</summary>
    public string KubeletVersion { get; set; } = "";

    /// <summary>The name the node registers under in its cluster.</summary>
    public string NodeName { get; set; } = "";

    /// <summary>Where the node name came from: certificate or hostname.</summary>
    public string NodeNameSource { get; set; } = "";

    /// <summary>Whether the host runs a kubelet, which makes it a worker node.</summary>
    public bool HasKubelet { get; set; }

    /// <summary>Whether the host runs control-plane components.</summary>
    public bool ControlPlane { get; set; }

    /// <summary>Whether a cluster-admin credential file exists on the host.</summary>
    public bool AdminCredentialPresent { get; set; }

    /// <summary>Whether this host has been enrolled as the reporter for its cluster.</summary>
    public bool ReporterEnrolled { get; set; }
}
