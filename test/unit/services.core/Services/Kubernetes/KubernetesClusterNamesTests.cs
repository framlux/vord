// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Kubernetes;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Tests for <see cref="KubernetesClusterNames"/>: the label shown for a distribution and the name
/// a cluster carries until an operator renames it.
/// </summary>
public class KubernetesClusterNamesTests
{
    private const string Fingerprint = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";

    [Test]
    [Arguments("k3s", "k3s")]
    [Arguments("rke2", "RKE2")]
    [Arguments("kubeadm", "kubeadm")]
    [Arguments("k0s", "k0s")]
    [Arguments("microk8s", "MicroK8s")]
    [Arguments("unknown", "Kubernetes")]
    [Arguments("something-new", "Kubernetes")]
    [Arguments("", "Kubernetes")]
    public async Task DistroLabel_MapsEachKnownDistroAndFallsBackToKubernetes(string distro, string expected)
    {
        string label = KubernetesClusterNames.DistroLabel(distro);

        await Assert.That(label).IsEqualTo(expected);
    }

    [Test]
    public async Task Derive_ControlPlaneNodeWinsOverLexicographicallySmallerWorker()
    {
        // Intent: the name identifies the cluster by its control plane, so a worker that sorts first
        // must not take the name from a control-plane node.
        (string NodeName, bool ControlPlane)[] nodes =
        [
            ("a-worker", false),
            ("m-control", true),
            ("z-control", true),
        ];

        string name = KubernetesClusterNames.Derive("k3s", Fingerprint, nodes);

        await Assert.That(name).IsEqualTo("k3s m-control");
    }

    [Test]
    public async Task Derive_NoControlPlane_UsesTheSmallestNodeName()
    {
        (string NodeName, bool ControlPlane)[] nodes =
        [
            ("worker-b", false),
            ("worker-a", false),
        ];

        string name = KubernetesClusterNames.Derive("rke2", Fingerprint, nodes);

        await Assert.That(name).IsEqualTo("RKE2 worker-a");
    }

    [Test]
    public async Task Derive_NoNodes_UsesTheFirstTwelveFingerprintCharacters()
    {
        string name = KubernetesClusterNames.Derive("microk8s", Fingerprint, []);

        await Assert.That(name).IsEqualTo("MicroK8s a1b2c3d4e5f6");
    }

    [Test]
    public async Task Derive_UnknownDistro_IsLabelledKubernetes()
    {
        string name = KubernetesClusterNames.Derive("unknown", Fingerprint, [("node-1", true)]);

        await Assert.That(name).IsEqualTo("Kubernetes node-1");
    }

    [Test]
    public async Task Derive_ComparesNodeNamesOrdinally()
    {
        // Intent: ordering must not depend on culture or letter case, so every replica derives the
        // same name. In ordinal order every upper-case letter sorts before every lower-case one.
        (string NodeName, bool ControlPlane)[] nodes =
        [
            ("alpha", true),
            ("Zulu", true),
        ];

        string name = KubernetesClusterNames.Derive("kubeadm", Fingerprint, nodes);

        await Assert.That(name).IsEqualTo("kubeadm Zulu");
    }

    [Test]
    public async Task Derive_FingerprintShorterThanTwelveCharacters_UsesItWhole()
    {
        string name = KubernetesClusterNames.Derive("k0s", "abc", []);

        await Assert.That(name).IsEqualTo("k0s abc");
    }
}
