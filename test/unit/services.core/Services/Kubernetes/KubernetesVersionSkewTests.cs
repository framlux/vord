// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Kubernetes;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Tests for <see cref="KubernetesVersionSkew"/>: when a node's kubelet is far enough from the cluster's
/// version to be flagged.
/// </summary>
public class KubernetesVersionSkewTests
{
    [Test]
    [Arguments("v1.34.1+k3s1", "v1.34.1+k3s1", false)]
    [Arguments("v1.34.0+k3s1", "v1.34.9+k3s1", false)]
    [Arguments("v1.33.4+k3s1", "v1.34.1+k3s1", false)]
    [Arguments("v1.32.4+k3s1", "v1.34.1+k3s1", false)]
    [Arguments("v1.31.4+k3s1", "v1.34.1+k3s1", false)]
    [Arguments("v1.30.4+k3s1", "v1.34.1+k3s1", true)]
    [Arguments("v1.20.0+k3s1", "v1.34.1+k3s1", true)]
    public async Task IsKubeletSkewed_KubeletBehindTheCluster_IsFlaggedOnlyBeyondThreeMinorVersions(
        string kubelet, string cluster, bool expected)
    {
        // Intent: three minor versions behind is the supported limit, so it is the fourth that is flagged.
        await Assert.That(KubernetesVersionSkew.IsKubeletSkewed(kubelet, cluster)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("v1.35.0+k3s1", "v1.34.1+k3s1")]
    [Arguments("v1.31.0", "v1.30.9")]
    public async Task IsKubeletSkewed_KubeletNewerThanTheCluster_IsFlagged(string kubelet, string cluster)
    {
        // Intent: a kubelet must never be newer than the API server it registers with.
        await Assert.That(KubernetesVersionSkew.IsKubeletSkewed(kubelet, cluster)).IsTrue();
    }

    [Test]
    [Arguments("v1.30.4+rke2r1", "v1.30.4+rke2r1")]
    [Arguments("1.30.4", "v1.30.4")]
    [Arguments("v1.30", "v1.30.4+k3s1")]
    [Arguments("v1.30.4-eks-1a2b3c", "v1.30.4-eks-1a2b3c")]
    public async Task IsKubeletSkewed_AcceptsTheVersionSpellingsDistributionsUse(string kubelet, string cluster)
    {
        await Assert.That(KubernetesVersionSkew.IsKubeletSkewed(kubelet, cluster)).IsFalse();
    }

    [Test]
    [Arguments(null, "v1.34.1+k3s1")]
    [Arguments("v1.30.4+k3s1", null)]
    [Arguments(null, null)]
    [Arguments("", "v1.34.1+k3s1")]
    [Arguments("v1.30.4+k3s1", "")]
    [Arguments("not-a-version", "v1.34.1+k3s1")]
    [Arguments("v1.30.4+k3s1", "latest")]
    [Arguments("v1.x.4", "v1.34.1+k3s1")]
    [Arguments("v1", "v1.34.1+k3s1")]
    [Arguments("v1.99999999999999999999.0", "v1.34.1+k3s1")]
    public async Task IsKubeletSkewed_EitherVersionMissingOrUnparseable_IsNotFlagged(string? kubelet, string? cluster)
    {
        // Intent: a flag is a claim about two known versions, so nothing is claimed when one is not known.
        await Assert.That(KubernetesVersionSkew.IsKubeletSkewed(kubelet, cluster)).IsFalse();
    }
}
