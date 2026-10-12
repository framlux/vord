// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Builds the names a Kubernetes cluster is shown under until an operator renames it.
/// </summary>
public static class KubernetesClusterNames
{
    private const int FingerprintPrefixLength = 12;

    /// <summary>
    /// Returns the label for a distribution: its usual spelling for the distributions Vord detects,
    /// and "Kubernetes" for anything else.
    /// </summary>
    /// <param name="distro">The distribution value a machine reported.</param>
    public static string DistroLabel(string distro)
    {
        return distro switch
        {
            "k3s" => "k3s",
            "rke2" => "RKE2",
            "kubeadm" => "kubeadm",
            "k0s" => "k0s",
            "microk8s" => "MicroK8s",
            _ => "Kubernetes",
        };
    }

    /// <summary>
    /// Derives a cluster's default name, "&lt;label&gt; &lt;name&gt;". The name is the smallest control-plane
    /// node name when there is one, else the smallest node name, else the start of the CA fingerprint.
    /// Names are compared ordinally so every replica derives the same name.
    /// </summary>
    /// <param name="distro">The cluster's distribution.</param>
    /// <param name="caFingerprint">The cluster's CA fingerprint.</param>
    /// <param name="nodes">The cluster's nodes, each with whether it runs a control plane.</param>
    public static string Derive(string distro, string caFingerprint, IEnumerable<(string NodeName, bool ControlPlane)> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        List<(string NodeName, bool ControlPlane)> named = nodes.Where(n => string.IsNullOrEmpty(n.NodeName) == false).ToList();

        string? name = SmallestOrdinal(named.Where(n => n.ControlPlane).Select(n => n.NodeName))
            ?? SmallestOrdinal(named.Select(n => n.NodeName))
            ?? FingerprintPrefix(caFingerprint);

        return DistroLabel(distro) + " " + name;
    }

    private static string? SmallestOrdinal(IEnumerable<string> names)
    {
        string? smallest = null;
        foreach (string name in names)
        {
            if ((smallest == null) || (string.CompareOrdinal(name, smallest) < 0))
            {
                smallest = name;
            }
        }

        return smallest;
    }

    private static string FingerprintPrefix(string caFingerprint)
    {
        if (caFingerprint.Length <= FingerprintPrefixLength)
        {
            return caFingerprint;
        }

        return caFingerprint[..FingerprintPrefixLength];
    }
}
