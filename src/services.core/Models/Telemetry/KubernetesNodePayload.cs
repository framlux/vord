// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// KubernetesNode telemetry payload (type=14).
/// </summary>
public sealed class KubernetesNodePayload
{
    /// <summary>The Kubernetes distribution detected on the host (k3s, rke2, kubeadm, k0s, microk8s or unknown).</summary>
    public string Distro { get; set; } = "";

    /// <summary>The kubelet version found on the host, empty when none was found.</summary>
    public string KubeletVersion { get; set; } = "";

    /// <summary>The name the node registers under in its cluster.</summary>
    public string NodeName { get; set; } = "";

    /// <summary>Where the node name came from (certificate or hostname).</summary>
    public string NodeNameSource { get; set; } = "";

    /// <summary>Whether the host runs a kubelet, which makes it a worker node.</summary>
    public bool HasKubelet { get; set; }

    /// <summary>Whether the host runs control-plane components.</summary>
    public bool ControlPlane { get; set; }

    /// <summary>The lowercase hex SHA-256 fingerprint of the cluster CA certificate, empty when none was found.</summary>
    public string CaFingerprint { get; set; } = "";

    /// <summary>Whether a cluster-admin credential file exists on the host.</summary>
    public bool AdminCredentialPresent { get; set; }

    /// <summary>Whether this host has been enrolled as the reporter for its cluster.</summary>
    public bool ReporterEnrolled { get; set; }

    /// <summary>
    /// Why the cluster reporter on this host cannot use its credentials: <c>credentials_rejected</c> or
    /// <c>tls_ca_changed</c>. Empty or null when the last call to the cluster worked, or when the agent
    /// predates the field.
    /// </summary>
    public string? ReporterError { get; set; }
}
