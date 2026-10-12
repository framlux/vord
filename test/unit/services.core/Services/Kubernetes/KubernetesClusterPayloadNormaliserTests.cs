// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using TUnit.Assertions.Enums;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Tests for <see cref="KubernetesClusterPayloadNormaliser"/>, which turns a stored cluster snapshot into
/// the snapshot the inventory applies. The payload comes from an agent the server does not control, so
/// everything that identifies a cluster, orders a snapshot or has to fit a column is checked here.
/// </summary>
public class KubernetesClusterPayloadNormaliserTests
{
    private const string Fingerprint = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";
    private const string Uid = "11111111-2222-4333-8444-555555555555";

    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static MachineTelemetry TelemetryRow()
    {
        return new MachineTelemetry
        {
            Id = 77,
            MachineId = 9,
            TenantId = 3,
            TelemetryType = 15,
            Payload = "{}",
            ReceivedAt = Start,
            ServerReceivedAt = Start.AddSeconds(2),
            SourceEventId = "event-77",
        };
    }

    private static KubernetesClusterPayload ValidPayload()
    {
        return new KubernetesClusterPayload
        {
            KubeSystemUid = Uid,
            ClusterVersion = "v1.31.4+k3s1",
            RoleVersion = 1,
            RoleOutdated = true,
            CaFingerprint = Fingerprint,
            LeaseTransitions = 5,
            Truncated = true,
            LeaseUid = "lease-uid-1",
            LeaseCreatedAt = "2026-10-11T08:00:00Z",
            Nodes =
            [
                new KubernetesClusterNodeDto
                {
                    Name = "cp-1",
                    Roles = ["control-plane", "etcd"],
                    ReadyStatus = "True",
                    ReadySince = "2026-10-11T10:00:00Z",
                    Conditions = [new KubernetesNodeConditionDto { Type = "MemoryPressure", Status = "True", LastTransitionTime = "2026-10-11T11:00:00Z" }],
                    Unschedulable = true,
                    MachineId = "0123456789abcdef0123456789abcdef",
                    KubeletVersion = "v1.31.4+k3s1",
                    ContainerRuntimeVersion = "containerd://1.7.23-k3s2",
                    PodCount = 12,
                    PodCapacity = 110,
                },
            ],
        };
    }

    private static KubernetesClusterNodeDto NodeNamed(string name)
    {
        return new KubernetesClusterNodeDto { Name = name };
    }

    private static ClusterSnapshot Normalise(KubernetesClusterPayload payload)
    {
        return KubernetesClusterPayloadNormaliser.Normalise(payload, TelemetryRow())
            ?? throw new InvalidOperationException("The payload was expected to be usable.");
    }

    [Test]
    public async Task Normalise_ValidPayload_MapsEveryFieldAndTheRow()
    {
        ClusterSnapshot snapshot = Normalise(ValidPayload());

        await Assert.That(snapshot.TelemetryId).IsEqualTo(77L);
        await Assert.That(snapshot.TenantId).IsEqualTo(3);
        await Assert.That(snapshot.MachineId).IsEqualTo(9L);
        await Assert.That(snapshot.ServerReceivedAt).IsEqualTo(Start.AddSeconds(2));
        await Assert.That(snapshot.KubeSystemUid).IsEqualTo(Uid);
        await Assert.That(snapshot.CaFingerprint).IsEqualTo(Fingerprint);
        await Assert.That(snapshot.ClusterVersion).IsEqualTo("v1.31.4+k3s1");
        await Assert.That(snapshot.RoleOutdated).IsTrue();
        await Assert.That(snapshot.LeaseTransitions).IsEqualTo(5L);
        await Assert.That(snapshot.LeaseUid).IsEqualTo("lease-uid-1");
        await Assert.That(snapshot.LeaseCreatedAt).IsEqualTo(new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero));
        await Assert.That(snapshot.ReporterError).IsNull();
        await Assert.That(snapshot.Truncated).IsTrue();

        await Assert.That(snapshot.Nodes.Count).IsEqualTo(1);
        ClusterSnapshotNode node = snapshot.Nodes[0];
        await Assert.That(node.Name).IsEqualTo("cp-1");
        await Assert.That(node.Roles).IsEquivalentTo(new[] { "control-plane", "etcd" }, CollectionOrdering.Matching);
        await Assert.That(node.ReadyStatus).IsEqualTo("True");
        await Assert.That(node.ReadySince).IsEqualTo(new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero));
        await Assert.That(node.Conditions).IsEquivalentTo(
            new[] { new ClusterSnapshotCondition("MemoryPressure", "True", new DateTimeOffset(2026, 10, 11, 11, 0, 0, TimeSpan.Zero)) });
        await Assert.That(node.Unschedulable).IsTrue();
        await Assert.That(node.MachineId).IsEqualTo("0123456789abcdef0123456789abcdef");
        await Assert.That(node.KubeletVersion).IsEqualTo("v1.31.4+k3s1");
        await Assert.That(node.ContainerRuntimeVersion).IsEqualTo("containerd://1.7.23-k3s2");
        await Assert.That(node.PodCount).IsEqualTo(12);
        await Assert.That(node.PodCapacity).IsEqualTo(110);
    }

    [Test]
    public async Task Normalise_UidInTheCanonicalShape_IsKeptAndLowercased()
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.KubeSystemUid = "ABCDEF01-2222-4333-8444-555555ABCDEF";

        await Assert.That(Normalise(payload).KubeSystemUid).IsEqualTo("abcdef01-2222-4333-8444-555555abcdef");
    }

    [Test]
    [Arguments("")]
    [Arguments("11111111-2222-4333-8444-55555555555")]
    [Arguments("11111111-2222-4333-8444-5555555555555")]
    [Arguments("11111111222243338444555555555555")]
    [Arguments("11111111-2222-4333-8444-55555555555g")]
    [Arguments("11111111_2222_4333_8444_555555555555")]
    [Arguments("{11111111-2222-4333-8444-555555555555}")]
    [Arguments("not-a-uid")]
    public async Task Normalise_UidNotInTheCanonicalShape_BecomesNull(string uid)
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.KubeSystemUid = uid;

        await Assert.That(Normalise(payload).KubeSystemUid).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("ab")]
    [Arguments("A1B2C3D4E5F60718A1B2C3D4E5F60718A1B2C3D4E5F60718A1B2C3D4E5F60718")]
    [Arguments("a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f6071")]
    [Arguments("a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f607189")]
    [Arguments("g1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718")]
    public async Task Normalise_FingerprintNotSixtyFourLowercaseHexCharacters_BecomesNull(string fingerprint)
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.CaFingerprint = fingerprint;

        await Assert.That(Normalise(payload).CaFingerprint).IsNull();
    }

    [Test]
    [Arguments("credentials_rejected")]
    [Arguments("tls_ca_changed")]
    [Arguments("api_unreachable")]
    [Arguments("namespace_terminating")]
    public async Task Normalise_KnownReporterError_PassesThrough(string reporterError)
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.ReporterError = reporterError;

        await Assert.That(Normalise(payload).ReporterError).IsEqualTo(reporterError);
    }

    [Test]
    [Arguments("something_new")]
    [Arguments("API_UNREACHABLE")]
    [Arguments("credentials_rejected ")]
    [Arguments("x")]
    public async Task Normalise_UnknownReporterError_BecomesApiUnreachable(string reporterError)
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.ReporterError = reporterError;

        await Assert.That(Normalise(payload).ReporterError).IsEqualTo("api_unreachable");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Normalise_NoReporterError_IsASnapshotOfTheCluster(string? reporterError)
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.ReporterError = reporterError;

        ClusterSnapshot snapshot = Normalise(payload);

        await Assert.That(snapshot.ReporterError).IsNull();
        await Assert.That(snapshot.Nodes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Normalise_ErrorSnapshot_ListsNoNodes()
    {
        // Intent: an error snapshot says nothing about the nodes, so whatever nodes a payload carries
        // next to an error are not passed on to the inventory.
        KubernetesClusterPayload payload = ValidPayload();
        payload.ReporterError = "tls_ca_changed";

        ClusterSnapshot snapshot = Normalise(payload);

        await Assert.That(snapshot.ReporterError).IsEqualTo("tls_ca_changed");
        await Assert.That(snapshot.Nodes.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("yesterday")]
    [Arguments("2026-13-45T00:00:00Z")]
    public async Task Normalise_LeaseCreatedAtNotATimestamp_IsRejected(string leaseCreatedAt)
    {
        // Intent: the Lease's creation time orders a snapshot against the cluster's others, so a snapshot
        // without one cannot be fenced and is not applied.
        KubernetesClusterPayload payload = ValidPayload();
        payload.LeaseCreatedAt = leaseCreatedAt;

        await Assert.That(KubernetesClusterPayloadNormaliser.Normalise(payload, TelemetryRow())).IsNull();
    }

    [Test]
    public async Task Normalise_LeaseCreatedAtWithinTenMinutesAfterReceipt_IsAccepted()
    {
        // Intent: clocks differ between the agent and the server, so a Lease created a little after the
        // snapshot reached the server is tolerated, up to and including ten minutes.
        KubernetesClusterPayload payload = ValidPayload();
        payload.LeaseCreatedAt = TelemetryRow().ServerReceivedAt.AddMinutes(10).ToString("O");

        ClusterSnapshot snapshot = Normalise(payload);

        await Assert.That(snapshot.LeaseCreatedAt).IsEqualTo(TelemetryRow().ServerReceivedAt.AddMinutes(10));
    }

    [Test]
    [Arguments(10, 1)]
    [Arguments(60, 0)]
    [Arguments(60 * 24 * 365 * 70, 0)]
    public async Task Normalise_LeaseCreatedAtMoreThanTenMinutesAfterReceipt_IsRejected(int minutes, int seconds)
    {
        // Intent: a Lease cannot have been created after the snapshot that describes it reached the server.
        // A bogus future epoch would outrank every later snapshot in the per-epoch fence and silence the
        // cluster, so it is unusable like any other value that cannot be ordered.
        KubernetesClusterPayload payload = ValidPayload();
        payload.LeaseCreatedAt = TelemetryRow().ServerReceivedAt.AddMinutes(minutes).AddSeconds(seconds).ToString("O");

        await Assert.That(KubernetesClusterPayloadNormaliser.Normalise(payload, TelemetryRow())).IsNull();
    }

    [Test]
    public async Task Normalise_NegativeLeaseTransitions_IsRejected()
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.LeaseTransitions = -1;

        await Assert.That(KubernetesClusterPayloadNormaliser.Normalise(payload, TelemetryRow())).IsNull();
    }

    [Test]
    public async Task Normalise_ZeroLeaseTransitions_IsAccepted()
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.LeaseTransitions = 0;

        await Assert.That(Normalise(payload).LeaseTransitions).IsEqualTo(0L);
    }

    [Test]
    public async Task Normalise_LeaseCreatedAtWithAnOffset_IsTheSameInstantInUtc()
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.LeaseCreatedAt = "2026-10-11T10:00:00+02:00";

        ClusterSnapshot snapshot = Normalise(payload);

        await Assert.That(snapshot.LeaseCreatedAt).IsEqualTo(new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero));
        await Assert.That(snapshot.LeaseCreatedAt.Offset).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task Normalise_LeaseCreatedAtWithoutAnOffset_IsReadAsUtc()
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.LeaseCreatedAt = "2026-10-11T08:00:00";

        await Assert.That(Normalise(payload).LeaseCreatedAt).IsEqualTo(new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Normalise_EmptyLeaseUidClusterVersionAndMachineId_BecomeNull()
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.LeaseUid = "";
        payload.ClusterVersion = "";
        payload.Nodes[0].MachineId = "";
        payload.Nodes[0].ReadyStatus = "";
        payload.Nodes[0].ContainerRuntimeVersion = "";
        payload.Nodes[0].KubeletVersion = "";

        ClusterSnapshot snapshot = Normalise(payload);

        await Assert.That(snapshot.LeaseUid).IsNull();
        await Assert.That(snapshot.ClusterVersion).IsNull();
        await Assert.That(snapshot.Nodes[0].MachineId).IsNull();
        await Assert.That(snapshot.Nodes[0].ReadyStatus).IsNull();
        await Assert.That(snapshot.Nodes[0].ContainerRuntimeVersion).IsNull();
        await Assert.That(snapshot.Nodes[0].KubeletVersion).IsNull();
    }

    [Test]
    public async Task Normalise_OverLongStrings_AreCutToTheirColumns()
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.ClusterVersion = new string('v', 100);
        payload.LeaseUid = new string('l', 100);
        payload.Nodes[0].MachineId = new string('m', 100);
        payload.Nodes[0].ReadyStatus = new string('r', 100);
        payload.Nodes[0].ContainerRuntimeVersion = new string('c', 100);
        payload.Nodes[0].KubeletVersion = new string('k', 100);
        payload.Nodes[0].Roles = [new string('x', 100)];
        payload.Nodes[0].Conditions = [new KubernetesNodeConditionDto { Type = new string('t', 100), Status = new string('s', 100) }];

        ClusterSnapshot snapshot = Normalise(payload);

        await Assert.That(snapshot.ClusterVersion).IsEqualTo(new string('v', 64));
        await Assert.That(snapshot.LeaseUid).IsEqualTo(new string('l', 64));
        ClusterSnapshotNode node = snapshot.Nodes[0];
        await Assert.That(node.MachineId).IsEqualTo(new string('m', 64));
        await Assert.That(node.ReadyStatus).IsEqualTo(new string('r', 16));
        await Assert.That(node.ContainerRuntimeVersion).IsEqualTo(new string('c', 64));
        await Assert.That(node.KubeletVersion).IsEqualTo(new string('k', 64));
        await Assert.That(node.Roles).IsEquivalentTo(new[] { new string('x', 63) });
        await Assert.That(node.Conditions[0].Type).IsEqualTo(new string('t', 63));
        await Assert.That(node.Conditions[0].Status).IsEqualTo(new string('s', 16));
    }

    [Test]
    public async Task Normalise_StringsAtTheirLimit_AreKept()
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.ClusterVersion = new string('v', 64);
        payload.Nodes[0].ReadyStatus = new string('r', 16);

        ClusterSnapshot snapshot = Normalise(payload);

        await Assert.That(snapshot.ClusterVersion).IsEqualTo(new string('v', 64));
        await Assert.That(snapshot.Nodes[0].ReadyStatus).IsEqualTo(new string('r', 16));
    }

    [Test]
    public async Task Normalise_TruncationNeverSplitsASurrogatePair()
    {
        // Intent: cutting between the halves of a surrogate pair would leave a character no database
        // encodes, so the cut moves back one character.
        KubernetesClusterPayload payload = ValidPayload();
        payload.ClusterVersion = new string('v', 63) + "\U0001F600";

        string? version = Normalise(payload).ClusterVersion;

        await Assert.That(version).IsEqualTo(new string('v', 63));
    }

    [Test]
    [Arguments("Node_1")]
    [Arguments("-leading")]
    [Arguments("trailing-")]
    [Arguments("UPPER")]
    [Arguments("under_score")]
    [Arguments("double..dot")]
    [Arguments("")]
    public async Task Normalise_NodeNameThatIsNotAKubernetesNodeName_IsDropped(string name)
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.Nodes = [NodeNamed(name), NodeNamed("worker-1")];

        ClusterSnapshot snapshot = Normalise(payload);

        await Assert.That(snapshot.Nodes.Select(n => n.Name)).IsEquivalentTo(new[] { "worker-1" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Normalise_NodeNameLengths_The253CharacterLimitIsInclusive()
    {
        string longest = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
        KubernetesClusterPayload payload = ValidPayload();
        payload.Nodes = [NodeNamed(longest), NodeNamed(longest + "e")];

        ClusterSnapshot snapshot = Normalise(payload);

        await Assert.That(longest.Length).IsEqualTo(253);
        await Assert.That(snapshot.Nodes.Select(n => n.Name)).IsEquivalentTo(new[] { longest }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Normalise_NullEntryInTheNodeList_IsDropped()
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.Nodes = [null!, NodeNamed("worker-1")];

        ClusterSnapshot snapshot = Normalise(payload);

        await Assert.That(snapshot.Nodes.Select(n => n.Name)).IsEquivalentTo(new[] { "worker-1" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Normalise_NullNodeList_IsAnEmptyOne()
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.Nodes = null!;

        await Assert.That(Normalise(payload).Nodes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Normalise_NodeTimestampsThatAreNotTimestamps_BecomeNull()
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.Nodes[0].ReadySince = "recently";
        payload.Nodes[0].Conditions[0].LastTransitionTime = "";

        ClusterSnapshot snapshot = Normalise(payload);

        await Assert.That(snapshot.Nodes[0].ReadySince).IsNull();
        await Assert.That(snapshot.Nodes[0].Conditions[0].LastTransitionTime).IsNull();
    }

    [Test]
    [Arguments(-5, 0)]
    [Arguments(0, 0)]
    [Arguments(7, 7)]
    public async Task Normalise_PodCount_IsNeverNegative(int podCount, int expected)
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.Nodes[0].PodCount = podCount;

        await Assert.That(Normalise(payload).Nodes[0].PodCount).IsEqualTo(expected);
    }

    [Test]
    [Arguments(-1, null)]
    [Arguments(0, null)]
    [Arguments(1, 1)]
    [Arguments(110, 110)]
    public async Task Normalise_PodCapacityThatTheNodeDoesNotSay_IsNull(int podCapacity, int? expected)
    {
        KubernetesClusterPayload payload = ValidPayload();
        payload.Nodes[0].PodCapacity = podCapacity;

        await Assert.That(Normalise(payload).Nodes[0].PodCapacity).IsEqualTo(expected);
    }

    [Test]
    public async Task Normalise_Roles_DropEmptyAndDuplicateEntriesAndStopAtSixteen()
    {
        KubernetesClusterPayload payload = ValidPayload();
        List<string> roles = ["", "worker", "worker", null!];
        roles.AddRange(Enumerable.Range(0, 30).Select(i => $"role-{i:D2}"));
        payload.Nodes[0].Roles = roles;

        IReadOnlyList<string> normalised = Normalise(payload).Nodes[0].Roles;

        await Assert.That(normalised.Count).IsEqualTo(16);
        await Assert.That(normalised[0]).IsEqualTo("worker");
        await Assert.That(normalised[1]).IsEqualTo("role-00");
        await Assert.That(normalised.Distinct().Count()).IsEqualTo(16);
    }

    [Test]
    public async Task Normalise_Conditions_DropEntriesWithoutATypeAndStopAtSixteen()
    {
        KubernetesClusterPayload payload = ValidPayload();
        List<KubernetesNodeConditionDto> conditions =
        [
            new KubernetesNodeConditionDto { Type = "", Status = "True" },
            null!,
        ];
        conditions.AddRange(Enumerable.Range(0, 30).Select(i => new KubernetesNodeConditionDto { Type = $"Condition{i}", Status = "True" }));
        payload.Nodes[0].Conditions = conditions;

        IReadOnlyList<ClusterSnapshotCondition> normalised = Normalise(payload).Nodes[0].Conditions;

        await Assert.That(normalised.Count).IsEqualTo(16);
        await Assert.That(normalised[0].Type).IsEqualTo("Condition0");
    }

    [Test]
    public async Task Normalise_EmptyPayload_IsRejectedForWantingALeaseEpoch()
    {
        // Intent: an empty payload is what a snapshot the server failed to serialise looks like.
        await Assert.That(KubernetesClusterPayloadNormaliser.Normalise(new KubernetesClusterPayload(), TelemetryRow())).IsNull();
    }
}
