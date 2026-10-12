// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Kubernetes;

/// <summary>
/// A node of a Kubernetes cluster and the machine it is linked to, when there is one.
/// </summary>
public sealed class KubernetesNodeDto
{
    /// <summary>Node database ID.</summary>
    public long Id { get; set; }

    /// <summary>The Kubernetes node name.</summary>
    public string NodeName { get; set; } = "";

    /// <summary>True when the node's machine reported that it runs a control plane.</summary>
    public bool ControlPlane { get; set; }

    /// <summary>The kubelet version the node's machine reported, if any.</summary>
    public string? KubeletVersion { get; set; }

    /// <summary>The linked machine's ID, or null while the node is not managed by Vord.</summary>
    public long? MachineId { get; set; }

    /// <summary>The linked machine's name, or null while the node is not managed by Vord.</summary>
    public string? MachineName { get; set; }

    /// <summary>True while the node reports a CA fingerprint different from its cluster's.</summary>
    public bool FingerprintMismatch { get; set; }

    /// <summary>When the node's own machine last reported it, or null if only another source has.</summary>
    public DateTimeOffset? LastLocalReportAt { get; set; }
}
