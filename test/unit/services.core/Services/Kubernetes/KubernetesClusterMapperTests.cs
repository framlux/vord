// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Tests for <see cref="KubernetesClusterMapper"/>: how clusters, nodes and candidates become the
/// summary, detail and node views, including the reporting state and the node counts.
/// </summary>
public class KubernetesClusterMapperTests
{
    private const string Fingerprint = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static KubernetesCluster Cluster(
        string? reporterError = null,
        bool roleOutdated = false,
        DateTimeOffset? lastSnapshotReceivedAt = null,
        long? holder = null,
        string? clusterVersion = null,
        bool truncated = false,
        string? displayName = null,
        string? kubeSystemUid = null)
    {
        return new KubernetesCluster
        {
            Id = 7,
            TenantId = 1,
            CaFingerprint = Fingerprint,
            DisplayName = displayName,
            Distro = "k3s",
            FirstSeenAt = Now.AddDays(-2),
            LastSeenAt = Now.AddHours(-1),
            ReporterError = reporterError,
            RoleOutdated = roleOutdated,
            LastSnapshotReceivedAt = lastSnapshotReceivedAt,
            ActiveReporterMachineId = holder,
            ClusterVersion = clusterVersion,
            Truncated = truncated,
            KubeSystemUid = kubeSystemUid,
        };
    }

    private static KubernetesReporterCandidate Candidate(long machineId, string? reporterError = null)
    {
        return new KubernetesReporterCandidate
        {
            TenantId = 1,
            ClusterId = 7,
            MachineId = machineId,
            ReporterError = reporterError,
            LastReportAt = Now.AddMinutes(-3),
        };
    }

    private static KubernetesNodeWithMachine Node(
        string name,
        string? readyStatus = null,
        long? machineId = null,
        string? machineName = null,
        bool localControlPlane = false,
        string? roles = null,
        string? kubeletVersion = null,
        string? conditions = null)
    {
        return new KubernetesNodeWithMachine(
            new KubernetesNode
            {
                Id = 100,
                TenantId = 1,
                ClusterId = 7,
                NodeName = name,
                MachineId = machineId,
                ReadyStatus = readyStatus,
                LocalControlPlane = localControlPlane,
                Roles = roles,
                KubeletVersion = kubeletVersion,
                Conditions = conditions,
            },
            machineName);
    }

    // ========== ToSummary: reporting ==========

    [Test]
    public async Task ToSummary_NoCandidates_IsOffWithoutAHolder()
    {
        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            Cluster(), [Node("cp-1")], [], new Dictionary<long, string>(), Now);

        await Assert.That(summary.ReportingState).IsEqualTo("off");
        await Assert.That(summary.ActiveReporterMachineId).IsNull();
        await Assert.That(summary.ActiveReporterMachineName).IsNull();
        await Assert.That(summary.ReporterError).IsNull();
        await Assert.That(summary.StaleSince).IsNull();
        await Assert.That(summary.LastSnapshotReceivedAt).IsNull();
        await Assert.That(summary.ClusterVersion).IsNull();
        await Assert.That(summary.Truncated).IsFalse();
    }

    [Test]
    public async Task ToSummary_ActiveCluster_CarriesTheHolderTheVersionAndTheSnapshotTime()
    {
        DateTimeOffset snapshot = Now.AddMinutes(-4);
        KubernetesCluster cluster = Cluster(lastSnapshotReceivedAt: snapshot, holder: 21, clusterVersion: "v1.30.4+k3s1", truncated: true);

        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            cluster, [Node("cp-1")], [Candidate(21), Candidate(22)], new Dictionary<long, string> { [21] = "cp-host", [22] = "cp-two" }, Now);

        await Assert.That(summary.ReportingState).IsEqualTo("active");
        await Assert.That(summary.ActiveReporterMachineId).IsEqualTo(21L);
        await Assert.That(summary.ActiveReporterMachineName).IsEqualTo("cp-host");
        await Assert.That(summary.ClusterVersion).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(summary.Truncated).IsTrue();
        await Assert.That(summary.LastSnapshotReceivedAt).IsEqualTo(snapshot);
        await Assert.That(summary.StaleSince).IsNull();
        await Assert.That(summary.ReporterError).IsNull();
    }

    [Test]
    public async Task ToSummary_HolderWhoseNameIsUnknown_KeepsTheIdAndHasNoName()
    {
        KubernetesCluster cluster = Cluster(lastSnapshotReceivedAt: Now, holder: 21);

        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            cluster, [], [Candidate(22)], new Dictionary<long, string>(), Now);

        await Assert.That(summary.ActiveReporterMachineId).IsEqualTo(21L);
        await Assert.That(summary.ActiveReporterMachineName).IsNull();
    }

    [Test]
    public async Task ToSummary_StaleCluster_SaysSinceWhenTheLastSnapshotArrived()
    {
        DateTimeOffset snapshot = Now.AddHours(-3);

        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            Cluster(lastSnapshotReceivedAt: snapshot, holder: 21), [], [Candidate(21)], new Dictionary<long, string>(), Now);

        await Assert.That(summary.ReportingState).IsEqualTo("stale");
        await Assert.That(summary.StaleSince).IsEqualTo(snapshot);
    }

    [Test]
    public async Task ToSummary_EnrolledButNeverSnapshotted_IsStaleWithNoSinceTime()
    {
        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            Cluster(), [], [Candidate(21)], new Dictionary<long, string>(), Now);

        await Assert.That(summary.ReportingState).IsEqualTo("stale");
        await Assert.That(summary.StaleSince).IsNull();
        await Assert.That(summary.LastSnapshotReceivedAt).IsNull();
    }

    [Test]
    public async Task ToSummary_RoleOutdated_IsReportedAsSuch()
    {
        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            Cluster(roleOutdated: true, lastSnapshotReceivedAt: Now), [], [Candidate(21)], new Dictionary<long, string>(), Now);

        await Assert.That(summary.ReportingState).IsEqualTo("role_outdated");
    }

    [Test]
    public async Task ToSummary_ClusterError_IsErrorWithThatError()
    {
        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            Cluster("namespace_terminating", lastSnapshotReceivedAt: Now), [], [Candidate(21)], new Dictionary<long, string>(), Now);

        await Assert.That(summary.ReportingState).IsEqualTo("error");
        await Assert.That(summary.ReporterError).IsEqualTo("namespace_terminating");
    }

    [Test]
    public async Task ToSummary_CandidateError_IsErrorWithTheFirstCandidateByMachineName()
    {
        // Intent: a candidate that cannot reach the cluster puts the cluster in error even though the
        // holder is healthy, and with several the one whose machine name sorts first is reported.
        Dictionary<long, string> names = new() { [30] = "zeta-host", [31] = "alpha-host" };

        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            Cluster(lastSnapshotReceivedAt: Now, holder: 30),
            [],
            [Candidate(30, "tls_ca_changed"), Candidate(31, "credentials_rejected")],
            names,
            Now);

        await Assert.That(summary.ReportingState).IsEqualTo("error");
        await Assert.That(summary.ReporterError).IsEqualTo("credentials_rejected");
    }

    [Test]
    public async Task ToSummary_ErrorOnAStaleCluster_KeepsTheStaleSince()
    {
        DateTimeOffset snapshot = Now.AddHours(-6);

        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            Cluster("api_unreachable", lastSnapshotReceivedAt: snapshot), [], [Candidate(21)], new Dictionary<long, string>(), Now);

        await Assert.That(summary.ReportingState).IsEqualTo("error");
        await Assert.That(summary.StaleSince).IsEqualTo(snapshot);
    }

    // ========== ToSummary: node counts ==========

    [Test]
    public async Task ToSummary_CountsNodesByReadyStatus()
    {
        // Intent: True is ready, False is not ready, and anything else, including no status at all for
        // a node no snapshot has described, is unknown.
        KubernetesNodeWithMachine[] nodes =
        [
            Node("n1", "True"),
            Node("n2", "True"),
            Node("n3", "False"),
            Node("n4", "Unknown"),
            Node("n5", null),
            Node("n6", "true"),
            Node("n7", ""),
        ];

        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            Cluster(), nodes, [], new Dictionary<long, string>(), Now);

        await Assert.That(summary.ReadyCount).IsEqualTo(2);
        await Assert.That(summary.NotReadyCount).IsEqualTo(1);
        await Assert.That(summary.UnknownCount).IsEqualTo(4);
        await Assert.That(summary.NodeCount).IsEqualTo(7);
    }

    [Test]
    public async Task ToSummary_NoNodes_HasZeroCounts()
    {
        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            Cluster(), [], [], new Dictionary<long, string>(), Now);

        await Assert.That(summary.ReadyCount).IsEqualTo(0);
        await Assert.That(summary.NotReadyCount).IsEqualTo(0);
        await Assert.That(summary.UnknownCount).IsEqualTo(0);
    }

    [Test]
    public async Task ToSummary_ReporterControlPlaneRoleNamesTheCluster()
    {
        // Intent: nodes only the cluster reporter knows about name the cluster too.
        KubernetesNodeWithMachine[] nodes =
        [
            Node("a-worker", roles: "[\"worker\"]"),
            Node("z-agentless", roles: "[\"control-plane\",\"etcd\"]"),
        ];

        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            Cluster(), nodes, [], new Dictionary<long, string>(), Now);

        await Assert.That(summary.Name).IsEqualTo("k3s z-agentless");
    }

    [Test]
    public async Task ToSummary_MalformedRolesAreIgnoredWhenNaming()
    {
        KubernetesNodeWithMachine[] nodes =
        [
            Node("a-worker", roles: "not json"),
            Node("m-cp", localControlPlane: true, roles: "{\"unexpected\":true}"),
        ];

        KubernetesClusterSummaryDto summary = KubernetesClusterMapper.ToSummary(
            Cluster(), nodes, [], new Dictionary<long, string>(), Now);

        await Assert.That(summary.Name).IsEqualTo("k3s m-cp");
    }

    // ========== ToNodeDto ==========

    [Test]
    public async Task ToNodeDto_MapsEveryReporterField()
    {
        DateTimeOffset readySince = Now.AddDays(-4);
        DateTimeOffset cordoned = Now.AddHours(-2);
        DateTimeOffset seen = Now.AddMinutes(-9);
        KubernetesNode node = new()
        {
            Id = 55,
            TenantId = 1,
            ClusterId = 7,
            NodeName = "cp-1",
            MachineId = 21,
            LocalControlPlane = true,
            KubeletVersion = "v1.30.4+k3s1",
            Roles = "[\"control-plane\",\"etcd\"]",
            ReadyStatus = "True",
            ReadySince = readySince,
            Conditions = "[{\"type\":\"MemoryPressure\",\"status\":\"True\",\"lastTransitionTime\":\"2026-10-12T10:00:00Z\"}]",
            Unschedulable = true,
            UnschedulableSince = cordoned,
            ContainerRuntimeVersion = "containerd://1.7.23",
            PodCount = 14,
            PodCapacity = 110,
            LastReporterSeenAt = seen,
            LastLocalReportAt = Now,
        };

        KubernetesNodeDto dto = KubernetesClusterMapper.ToNodeDto(node, "cp-host", "v1.30.4+k3s1");

        await Assert.That(dto.Id).IsEqualTo(55L);
        await Assert.That(dto.NodeName).IsEqualTo("cp-1");
        await Assert.That(dto.MachineName).IsEqualTo("cp-host");
        await Assert.That(dto.Roles.ToArray()).IsEquivalentTo(new[] { "control-plane", "etcd" });
        await Assert.That(dto.ReadyStatus).IsEqualTo("True");
        await Assert.That(dto.ReadySince).IsEqualTo(readySince);
        await Assert.That(dto.Conditions.Count).IsEqualTo(1);
        await Assert.That(dto.Conditions[0].Type).IsEqualTo("MemoryPressure");
        await Assert.That(dto.Conditions[0].Status).IsEqualTo("True");
        await Assert.That(dto.Conditions[0].LastTransitionTime).IsEqualTo("2026-10-12T10:00:00Z");
        await Assert.That(dto.Unschedulable).IsTrue();
        await Assert.That(dto.UnschedulableSince).IsEqualTo(cordoned);
        await Assert.That(dto.ContainerRuntimeVersion).IsEqualTo("containerd://1.7.23");
        await Assert.That(dto.PodCount).IsEqualTo(14);
        await Assert.That(dto.PodCapacity).IsEqualTo(110);
        await Assert.That(dto.LastReporterSeenAt).IsEqualTo(seen);
        await Assert.That(dto.KubeletSkew).IsFalse();
    }

    [Test]
    public async Task ToNodeDto_NodeNoSnapshotHasListed_HasEmptyReporterFields()
    {
        KubernetesNode node = new() { Id = 1, TenantId = 1, ClusterId = 7, NodeName = "cp-1" };

        KubernetesNodeDto dto = KubernetesClusterMapper.ToNodeDto(node, null, "v1.30.4");

        await Assert.That(dto.Roles.Count).IsEqualTo(0);
        await Assert.That(dto.Conditions.Count).IsEqualTo(0);
        await Assert.That(dto.ReadyStatus).IsNull();
        await Assert.That(dto.ReadySince).IsNull();
        await Assert.That(dto.Unschedulable).IsFalse();
        await Assert.That(dto.UnschedulableSince).IsNull();
        await Assert.That(dto.ContainerRuntimeVersion).IsNull();
        await Assert.That(dto.PodCount).IsNull();
        await Assert.That(dto.PodCapacity).IsNull();
        await Assert.That(dto.LastReporterSeenAt).IsNull();
        await Assert.That(dto.KubeletSkew).IsFalse();
    }

    [Test]
    [Arguments("not json")]
    [Arguments("{\"a\":1}")]
    [Arguments("[1,2]")]
    [Arguments("")]
    public async Task ToNodeDto_UnreadableRolesAndConditions_BecomeEmptyLists(string stored)
    {
        // Intent: the columns hold text the projection wrote, but a value that cannot be read must
        // never turn the cluster page into an error.
        KubernetesNode node = new() { Id = 1, TenantId = 1, ClusterId = 7, NodeName = "cp-1", Roles = stored, Conditions = stored };

        KubernetesNodeDto dto = KubernetesClusterMapper.ToNodeDto(node, null, null);

        await Assert.That(dto.Roles.Count).IsEqualTo(0);
        await Assert.That(dto.Conditions.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("v1.30.4+k3s1", "v1.34.1+k3s1", true)]
    [Arguments("v1.31.4+k3s1", "v1.34.1+k3s1", false)]
    [Arguments("v1.35.0+k3s1", "v1.34.1+k3s1", true)]
    [Arguments("v1.30.4+k3s1", null, false)]
    [Arguments(null, "v1.34.1+k3s1", false)]
    public async Task ToNodeDto_KubeletSkewIsJudgedAgainstTheClusterVersion(string? kubelet, string? cluster, bool expected)
    {
        KubernetesNode node = new() { Id = 1, TenantId = 1, ClusterId = 7, NodeName = "cp-1", KubeletVersion = kubelet };

        KubernetesNodeDto dto = KubernetesClusterMapper.ToNodeDto(node, null, cluster);

        await Assert.That(dto.KubeletSkew).IsEqualTo(expected);
    }

    // ========== ToDetail ==========

    [Test]
    public async Task ToDetail_CarriesTheUidTheCandidatesAndTheEligibleMachines()
    {
        KubernetesCluster cluster = Cluster(lastSnapshotReceivedAt: Now, holder: 21, kubeSystemUid: "3f0c2d9e-aaaa-4bbb-8ccc-0123456789ab");
        KubernetesCandidateDto candidate = new() { MachineId = 21, MachineName = "cp-host", IsActiveReporter = true, Online = true, LastReportAt = Now };
        KubernetesEligibleMachineDto eligible = new() { MachineId = 22, MachineName = "cp-two", AuthorizedSigningKeyIds = [3, 4] };
        KubernetesReporterMachines machines = new([candidate], [eligible]);

        KubernetesClusterDetailDto detail = KubernetesClusterMapper.ToDetail(
            cluster, [Node("worker-b"), Node("worker-a")], [Candidate(21)], new Dictionary<long, string> { [21] = "cp-host" }, machines, Now);

        await Assert.That(detail.KubeSystemUid).IsEqualTo("3f0c2d9e-aaaa-4bbb-8ccc-0123456789ab");
        await Assert.That(detail.CaFingerprint).IsEqualTo(Fingerprint);
        await Assert.That(detail.Candidates.Count).IsEqualTo(1);
        await Assert.That(detail.Candidates[0].MachineId).IsEqualTo(21L);
        await Assert.That(detail.EligibleMachines.Count).IsEqualTo(1);
        await Assert.That(detail.EligibleMachines[0].MachineId).IsEqualTo(22L);
        await Assert.That(detail.Nodes.Select(n => n.NodeName).ToArray()).IsEquivalentTo(new[] { "worker-a", "worker-b" });
        await Assert.That(detail.ReportingState).IsEqualTo("active");
        await Assert.That(detail.ActiveReporterMachineName).IsEqualTo("cp-host");
    }

    [Test]
    public async Task ToDetail_NodeKubeletSkewUsesTheClustersVersion()
    {
        KubernetesCluster cluster = Cluster(clusterVersion: "v1.34.1+k3s1");

        KubernetesClusterDetailDto detail = KubernetesClusterMapper.ToDetail(
            cluster,
            [Node("old", kubeletVersion: "v1.30.4+k3s1"), Node("current", kubeletVersion: "v1.34.1+k3s1")],
            [],
            new Dictionary<long, string>(),
            new KubernetesReporterMachines([], []),
            Now);

        await Assert.That(detail.Nodes.Single(n => n.NodeName == "old").KubeletSkew).IsTrue();
        await Assert.That(detail.Nodes.Single(n => n.NodeName == "current").KubeletSkew).IsFalse();
    }
}
