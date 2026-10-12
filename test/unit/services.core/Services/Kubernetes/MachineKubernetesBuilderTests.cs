// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Tests for <see cref="MachineKubernetesBuilder"/>: how a machine's Kubernetes block is built from its
/// linked node, its latest local report and the tenant's clusters, and which repository reads that costs.
/// </summary>
public class MachineKubernetesBuilderTests
{
    private const string Fingerprint = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";

    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static KubernetesCluster Cluster(long id, string? displayName)
    {
        return new KubernetesCluster
        {
            Id = id,
            TenantId = 5,
            CaFingerprint = Fingerprint,
            DisplayName = displayName,
            Distro = "k3s",
            FirstSeenAt = T0,
            LastSeenAt = T0,
        };
    }

    private static KubernetesNode Node(long id, long clusterId, string name, long? machineId, bool controlPlane = false)
    {
        return new KubernetesNode
        {
            Id = id,
            TenantId = 5,
            ClusterId = clusterId,
            NodeName = name,
            MachineId = machineId,
            LocalControlPlane = controlPlane,
            LastLocalReportAt = T0,
        };
    }

    private static KubernetesNodePayload Payload(bool hasKubelet = true, string nodeName = "cp-1")
    {
        return new KubernetesNodePayload
        {
            Distro = "k3s",
            KubeletVersion = "v1.30.4+k3s1",
            NodeName = nodeName,
            NodeNameSource = "certificate",
            HasKubelet = hasKubelet,
            ControlPlane = true,
            CaFingerprint = Fingerprint,
        };
    }

    private static IKubernetesInventoryRepository LinkedNodeRepo(KubernetesCluster cluster, params KubernetesNode[] nodes)
    {
        IKubernetesInventoryRepository repo = Substitute.For<IKubernetesInventoryRepository>();
        repo.GetNodeForMachineAsync(5, 9, Arg.Any<CancellationToken>()).Returns(nodes[0]);
        repo.GetClusterAsync(5, cluster.Id, Arg.Any<CancellationToken>()).Returns(cluster);
        repo.ListNodesAsync(5, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(nodes.Select(n => new KubernetesNodeWithMachine(n, null)).ToList());

        return repo;
    }

    [Test]
    public async Task BuildAsync_LinkedNodeInANamedCluster_DoesNotReadTheClustersNodes()
    {
        // Intent: a cluster an operator has named needs none of its nodes to be shown, so the machine
        // page must not pay for loading them.
        KubernetesCluster cluster = Cluster(11, "Production");
        IKubernetesInventoryRepository repo = LinkedNodeRepo(cluster, Node(1, 11, "cp-1", 9, controlPlane: true));

        MachineKubernetesDto? block = await MachineKubernetesBuilder.BuildAsync(repo, 5, 9, "cp-host", Payload(), CancellationToken.None);

        await Assert.That(block!.ClusterName).IsEqualTo("Production");
        await Assert.That(block.ClusterId).IsEqualTo(11L);
        await repo.DidNotReceive().ListNodesAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BuildAsync_FingerprintHolderWithADisplayName_DoesNotReadTheClustersNodes()
    {
        // Intent: the same saving applies when the cluster is found through the CA fingerprint of a
        // host that is not a node.
        KubernetesCluster cluster = Cluster(11, "Production");
        IKubernetesInventoryRepository repo = Substitute.For<IKubernetesInventoryRepository>();
        repo.ListClustersByFingerprintAsync(5, Fingerprint, Arg.Any<CancellationToken>()).Returns([cluster]);

        MachineKubernetesDto? block = await MachineKubernetesBuilder.BuildAsync(
            repo, 5, 9, "cp-host", Payload(hasKubelet: false), CancellationToken.None);

        await Assert.That(block!.Grouping).IsEqualTo("grouped");
        await Assert.That(block.ClusterName).IsEqualTo("Production");
        await repo.DidNotReceive().ListNodesAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task BuildAsync_ClusterWithoutADisplayName_DerivesItsNameFromItsNodes(string? displayName)
    {
        // Intent: a cluster that has no usable display name is named from its nodes, so those nodes
        // are read. A blank name counts as no name, the same as everywhere else a cluster is named.
        KubernetesCluster cluster = Cluster(11, displayName);
        IKubernetesInventoryRepository repo = LinkedNodeRepo(
            cluster, Node(1, 11, "cp-1", 9, controlPlane: true), Node(2, 11, "worker-1", null));

        MachineKubernetesDto? block = await MachineKubernetesBuilder.BuildAsync(repo, 5, 9, "cp-host", Payload(), CancellationToken.None);

        await Assert.That(block!.ClusterName).IsEqualTo("k3s cp-1");
        await repo.Received(1).ListNodesAsync(5, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BuildAsync_PayloadThatCannotBeNormalised_IsTreatedAsAbsent()
    {
        // Intent: a stored report the projection would refuse (here a node name that is not a valid
        // Kubernetes name) is not shown either. With a linked node the block still describes the node,
        // without a local part.
        KubernetesCluster cluster = Cluster(11, "Production");
        IKubernetesInventoryRepository repo = LinkedNodeRepo(cluster, Node(1, 11, "cp-1", 9));

        MachineKubernetesDto? block = await MachineKubernetesBuilder.BuildAsync(
            repo, 5, 9, "cp-host", Payload(nodeName: "Not_A_Node"), CancellationToken.None);

        await Assert.That(block).IsNotNull();
        await Assert.That(block!.Local).IsNull();
        await Assert.That(block.Node).IsNotNull();
        await Assert.That(block.Grouping).IsEqualTo("grouped");
    }

    [Test]
    public async Task BuildAsync_PayloadThatCannotBeNormalisedAndNoNode_HasNoBlock()
    {
        IKubernetesInventoryRepository repo = Substitute.For<IKubernetesInventoryRepository>();

        MachineKubernetesDto? block = await MachineKubernetesBuilder.BuildAsync(
            repo, 5, 9, "cp-host", Payload(nodeName: "Not_A_Node"), CancellationToken.None);

        await Assert.That(block).IsNull();
        await repo.DidNotReceive().ListClustersByFingerprintAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BuildAsync_HostWithoutAKubelet_HidesTheNodeNameAndItsSource()
    {
        // Intent: a host without a kubelet is not a node, so the name it reports (typically its
        // hostname) is not shown as one; the rest of what it reported still is.
        IKubernetesInventoryRepository repo = Substitute.For<IKubernetesInventoryRepository>();
        repo.ListClustersByFingerprintAsync(5, Fingerprint, Arg.Any<CancellationToken>()).Returns([]);

        MachineKubernetesDto? block = await MachineKubernetesBuilder.BuildAsync(
            repo, 5, 9, "cp-host", Payload(hasKubelet: false, nodeName: "cp-1"), CancellationToken.None);

        await Assert.That(block!.Local).IsNotNull();
        await Assert.That(block.Local!.NodeName).IsNull();
        await Assert.That(block.Local.NodeNameSource).IsNull();
        await Assert.That(block.Local.HasKubelet).IsFalse();
        await Assert.That(block.Local.ControlPlane).IsTrue();
        await Assert.That(block.Local.Distro).IsEqualTo("k3s");
    }

    [Test]
    public async Task BuildAsync_HostWithAKubelet_ShowsTheNodeNameAndItsSource()
    {
        IKubernetesInventoryRepository repo = Substitute.For<IKubernetesInventoryRepository>();
        repo.ListClustersByFingerprintAsync(5, Fingerprint, Arg.Any<CancellationToken>()).Returns([]);

        MachineKubernetesDto? block = await MachineKubernetesBuilder.BuildAsync(
            repo, 5, 9, "cp-host", Payload(hasKubelet: true, nodeName: "cp-1"), CancellationToken.None);

        await Assert.That(block!.Local!.NodeName).IsEqualTo("cp-1");
        await Assert.That(block.Local.NodeNameSource).IsEqualTo("certificate");
    }

    [Test]
    public async Task BuildAsync_LocalBlock_CarriesTheNormalisedValues()
    {
        // Intent: the page shows what the projection would store: an unrecognised distro reads as
        // unknown and an over-long kubelet version is cut to the stored length.
        IKubernetesInventoryRepository repo = Substitute.For<IKubernetesInventoryRepository>();
        repo.ListClustersByFingerprintAsync(5, Fingerprint, Arg.Any<CancellationToken>()).Returns([]);
        KubernetesNodePayload payload = Payload();
        payload.Distro = "docker-desktop";
        payload.KubeletVersion = "v" + new string('1', 200);

        MachineKubernetesDto? block = await MachineKubernetesBuilder.BuildAsync(repo, 5, 9, "cp-host", payload, CancellationToken.None);

        await Assert.That(block!.Local!.Distro).IsEqualTo("unknown");
        await Assert.That(block.Local.KubeletVersion).IsEqualTo(payload.KubeletVersion[..64]);
    }

    [Test]
    public async Task BuildAsync_FingerprintThatIsNotLowercaseHex_IsUnknownAndNeverLookedUp()
    {
        // Intent: a fingerprint the projection would discard cannot be used to place the machine on
        // the page either, so the grouping is unknown and no cluster is searched for it.
        IKubernetesInventoryRepository repo = Substitute.For<IKubernetesInventoryRepository>();
        KubernetesNodePayload payload = Payload();
        payload.CaFingerprint = Fingerprint.ToUpperInvariant();

        MachineKubernetesDto? block = await MachineKubernetesBuilder.BuildAsync(repo, 5, 9, "cp-host", payload, CancellationToken.None);

        await Assert.That(block!.Grouping).IsEqualTo("unknown");
        await repo.DidNotReceive().ListClustersByFingerprintAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
