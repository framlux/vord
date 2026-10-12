// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for <see cref="IKubernetesInventoryRepository.ApplyClusterSnapshotAsync"/>: how a
/// cluster snapshot from the Lease holder is resolved to a cluster, separated from clusters that
/// share its CA, merged with split clusters, fenced per Lease epoch and applied to the cluster, its
/// candidates and its nodes. SQLite enforces the same unique indexes as production, so a merge that
/// would break one fails here.
/// </summary>
public class KubernetesClusterSnapshotRepositoryTests
{
    private const string FingerprintA = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";
    private const string FingerprintB = "0f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a6978";
    private const string FingerprintC = "c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00";
    private const string UidA = "11111111-1111-4111-8111-111111111111";
    private const string UidB = "22222222-2222-4222-8222-222222222222";

    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Epoch0 = T0.AddDays(-60);
    private static readonly DateTimeOffset Epoch1 = T0.AddDays(-30);
    private static readonly DateTimeOffset Epoch2 = T0.AddDays(-1);

    private static IKubernetesInventoryRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new Database.Repositories.DatabaseRepository(
            dbFactory.Context, NullLogger<Database.Repositories.DatabaseRepository>.Instance);
    }

    private static async Task<int> SeedTenantAsync(DatabaseContext db)
    {
        return await db.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant());
    }

    private static async Task<long> SeedMachineAsync(
        DatabaseContext db, int tenantId, bool isDeleted = false, string? systemId = null)
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId);
        machine.IsDeleted = isDeleted;
        if (systemId != null)
        {
            machine.SystemId = systemId;
        }

        return await db.InsertWithInt64IdentityAsync(machine);
    }

    private static async Task<long> SeedClusterAsync(
        DatabaseContext db,
        int tenantId,
        string fingerprint,
        string? uid = null,
        string? displayName = null,
        DateTimeOffset? leaseCreatedAt = null,
        long leaseTransitions = 0,
        DateTimeOffset? lastSnapshotAt = null,
        long? lastSnapshotId = null,
        long? activeReporter = null,
        string distro = "k3s",
        string? reporterError = null)
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = fingerprint,
            DisplayName = displayName,
            Distro = distro,
            FirstSeenAt = T0.AddDays(-5),
            LastSeenAt = T0.AddDays(-4),
            KubeSystemUid = uid,
            ClusterVersion = (uid == null) ? null : "v1.29.0",
            ReporterError = reporterError,
            LeaseUid = (leaseCreatedAt == null) ? null : "seeded-lease",
            LeaseCreatedAt = leaseCreatedAt,
            LeaseTransitions = leaseTransitions,
            LastSnapshotReceivedAt = lastSnapshotAt,
            LastSnapshotTelemetryId = lastSnapshotId,
            ActiveReporterMachineId = activeReporter,
        });
    }

    private static async Task<long> SeedNodeAsync(
        DatabaseContext db,
        int tenantId,
        long clusterId,
        string nodeName,
        long? machineId = null,
        DateTimeOffset? lastLocalReportAt = null,
        DateTimeOffset? lastReporterSeenAt = null,
        string? reportedFingerprint = null,
        string? kubeletVersion = null,
        string? nodeMachineId = null,
        bool unschedulable = false,
        DateTimeOffset? unschedulableSince = null,
        string? roles = null,
        bool localControlPlane = false)
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = nodeName,
            MachineId = machineId,
            LastLocalReportAt = lastLocalReportAt,
            LastReporterSeenAt = lastReporterSeenAt,
            ReportedCaFingerprint = reportedFingerprint,
            FingerprintMismatch = false,
            LocalControlPlane = localControlPlane,
            KubeletVersion = kubeletVersion,
            NodeMachineId = nodeMachineId,
            Roles = roles,
            ReadyStatus = (lastReporterSeenAt == null) ? null : "True",
            Unschedulable = unschedulable,
            UnschedulableSince = unschedulableSince,
        });
    }

    private static async Task SeedCandidateAsync(
        DatabaseContext db, int tenantId, long clusterId, long machineId, DateTimeOffset lastReportAt, string? reporterError = null)
    {
        await db.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            MachineId = machineId,
            ReporterError = reporterError,
            LastReportAt = lastReportAt,
        });
    }

    private static ClusterSnapshotNode Node(
        string name,
        string? machineId = null,
        bool unschedulable = false,
        string? kubeletVersion = "v1.30.4+k3s1",
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<ClusterSnapshotCondition>? conditions = null)
    {
        return new ClusterSnapshotNode(
            name,
            machineId,
            roles ?? ["worker"],
            "True",
            T0.AddDays(-1),
            conditions ?? [],
            unschedulable,
            kubeletVersion,
            "containerd://1.7.23",
            7,
            110);
    }

    private static ClusterSnapshot Snapshot(
        long telemetryId,
        int tenantId,
        long machineId,
        DateTimeOffset at,
        string? uid = UidA,
        string? fingerprint = FingerprintA,
        long transitions = 1,
        DateTimeOffset? leaseCreatedAt = null,
        string? leaseUid = "lease-1",
        string? reporterError = null,
        bool truncated = false,
        string? clusterVersion = "v1.30.4+k3s1",
        bool roleOutdated = false,
        IReadOnlyList<ClusterSnapshotNode>? nodes = null)
    {
        return new ClusterSnapshot(
            telemetryId, tenantId, machineId, at, uid, fingerprint, clusterVersion, roleOutdated, transitions,
            leaseUid, leaseCreatedAt ?? Epoch1, reporterError, truncated, nodes ?? []);
    }

    private static async Task<SnapshotOutcome> ApplyAsync(IKubernetesInventoryRepository repo, ClusterSnapshot snapshot)
    {
        return await repo.ApplyClusterSnapshotAsync(snapshot, snapshot.ServerReceivedAt.AddSeconds(5), CancellationToken.None);
    }

    private static async Task<KubernetesCluster> ClusterAsync(DatabaseContext db, long clusterId)
    {
        return await db.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
    }

    private static async Task<string> StateOfAsync(DatabaseContext db, long clusterId)
    {
        return JsonSerializer.Serialize(await ClusterAsync(db, clusterId));
    }

    private static async Task<Dictionary<string, KubernetesNode>> NodesOfAsync(DatabaseContext db, long clusterId)
    {
        List<KubernetesNode> nodes = await db.KubernetesNodes.Where(n => n.ClusterId == clusterId).ToListAsync();

        return nodes.ToDictionary(n => n.NodeName, StringComparer.Ordinal);
    }

    private static async Task<KubernetesReporterCandidate?> CandidateOfAsync(DatabaseContext db, long machineId)
    {
        return await db.KubernetesReporterCandidates.FirstOrDefaultAsync(c => c.MachineId == machineId);
    }

    private static async Task AssertNothingWrittenAsync(DatabaseContext db)
    {
        await Assert.That(await db.KubernetesClusters.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.KubernetesNodes.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.KubernetesReporterCandidates.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task Snapshot_MachineWithACandidateRow_UsesThatClusterAndTakesTheUid()
    {
        // Intent: the sender's candidate row is the strongest evidence of its cluster, even when the
        // cluster holds another CA fingerprint. The snapshot is applied to that cluster, which keeps its
        // id and name, and takes the snapshot's UID and fingerprint.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, displayName: "production");
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, machineId, T0.AddMinutes(-30));

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(10, tenantId, machineId, T0, nodes: [Node("cp-1")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
        KubernetesCluster cluster = await ClusterAsync(dbFactory.Context, clusterId);
        await Assert.That(cluster.DisplayName).IsEqualTo("production");
        await Assert.That(cluster.KubeSystemUid).IsEqualTo(UidA);
        await Assert.That(cluster.CaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That((await NodesOfAsync(dbFactory.Context, clusterId)).Keys).IsEquivalentTo(new[] { "cp-1" });
    }

    [Test]
    public async Task Snapshot_MachineWithANodeRowButNoCandidate_UsesTheNodesClusterAndCreatesTheCandidate()
    {
        // Intent: a machine that local reports linked to a node belongs to that node's cluster, so the
        // snapshot goes there and the machine becomes a candidate of it.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB);
        await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", machineId, lastLocalReportAt: T0.AddMinutes(-5));

        SnapshotOutcome outcome = await ApplyAsync(repo, Snapshot(10, tenantId, machineId, T0, nodes: [Node("cp-1")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(2);
        KubernetesCluster cluster = await ClusterAsync(dbFactory.Context, clusterId);
        await Assert.That(cluster.KubeSystemUid).IsEqualTo(UidA);
        await Assert.That(cluster.ActiveReporterMachineId).IsEqualTo(machineId);
        KubernetesReporterCandidate? candidate = await CandidateOfAsync(dbFactory.Context, machineId);
        await Assert.That(candidate!.ClusterId).IsEqualTo(clusterId);
    }

    [Test]
    public async Task Snapshot_UidHeldByACluster_ResolvesThatClusterForAnUnknownSender()
    {
        // Intent: a sender with no candidate row and no node is placed by the cluster's UID, which is
        // the cluster's identity, even when the cluster holds another CA fingerprint.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long holder = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, uid: UidA);
        long other = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);

        SnapshotOutcome outcome = await ApplyAsync(repo, Snapshot(10, tenantId, machineId, T0));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(2);
        await Assert.That((await CandidateOfAsync(dbFactory.Context, machineId))!.ClusterId).IsEqualTo(holder);
        await Assert.That((await ClusterAsync(dbFactory.Context, other)).KubeSystemUid).IsNull();
    }

    [Test]
    public async Task Snapshot_OneUidlessClusterHoldsTheFingerprint_IsAdoptedKeepingItsIdAndName()
    {
        // Intent: local reports usually found the cluster before any snapshot arrives. The snapshot adopts
        // the one UID-less cluster with its CA, so the cluster keeps its id (and URL) and its name.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "home lab");
        await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB);

        SnapshotOutcome outcome = await ApplyAsync(repo, Snapshot(10, tenantId, machineId, T0));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(2);
        KubernetesCluster adopted = await ClusterAsync(dbFactory.Context, clusterId);
        await Assert.That(adopted.DisplayName).IsEqualTo("home lab");
        await Assert.That(adopted.KubeSystemUid).IsEqualTo(UidA);
        await Assert.That((await CandidateOfAsync(dbFactory.Context, machineId))!.ClusterId).IsEqualTo(clusterId);
    }

    [Test]
    public async Task Snapshot_SeveralUidlessClustersHoldTheFingerprint_CreatesAClusterInsteadOfGuessing()
    {
        // Intent: two UID-less clusters signed by one CA cannot be told apart by the CA, so the snapshot
        // adopts neither and founds a cluster for its own UID. Their disjoint node names give it no
        // reason to merge them.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long first = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long second = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedNodeAsync(dbFactory.Context, tenantId, first, "x", lastLocalReportAt: T0.AddMinutes(-3));
        await SeedNodeAsync(dbFactory.Context, tenantId, second, "y", lastLocalReportAt: T0.AddMinutes(-3));

        SnapshotOutcome outcome = await ApplyAsync(repo, Snapshot(10, tenantId, machineId, T0, nodes: [Node("z")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        List<KubernetesCluster> clusters = await dbFactory.Context.KubernetesClusters.OrderBy(c => c.Id).ToListAsync();
        await Assert.That(clusters.Count).IsEqualTo(3);
        await Assert.That(clusters[0].KubeSystemUid).IsNull();
        await Assert.That(clusters[1].KubeSystemUid).IsNull();
        await Assert.That(clusters[2].KubeSystemUid).IsEqualTo(UidA);
        await Assert.That((await CandidateOfAsync(dbFactory.Context, machineId))!.ClusterId).IsEqualTo(clusters[2].Id);
    }

    [Test]
    public async Task Snapshot_TheFingerprintBelongsToAClusterWithAnotherUid_IsNotAdopted()
    {
        // Intent: a cluster that already has a UID is a different cluster, even when it shares the CA. It
        // must not be adopted, and its UID must never be overwritten.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long holder = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidB);
        string before = await StateOfAsync(dbFactory.Context, holder);

        SnapshotOutcome outcome = await ApplyAsync(repo, Snapshot(10, tenantId, machineId, T0));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        await Assert.That(await StateOfAsync(dbFactory.Context, holder)).IsEqualTo(before);
        KubernetesCluster created = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id != holder);
        await Assert.That(created.KubeSystemUid).IsEqualTo(UidA);
    }

    [Test]
    public async Task Snapshot_NoClusterMatches_FoundsOneWithTheSnapshotsIdentityAndLeaseState()
    {
        // Intent: a cluster nothing has described yet is founded from its first snapshot, stamped with the
        // time the projection applied it, with the distro unknown until a local report names it, and the
        // sender becomes its active reporter and a candidate.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        DateTimeOffset now = T0.AddMinutes(2);

        SnapshotOutcome outcome = await repo.ApplyClusterSnapshotAsync(
            Snapshot(10, tenantId, machineId, T0, transitions: 4, leaseUid: "lease-x", clusterVersion: "v1.31.0", roleOutdated: true, truncated: true,
                nodes: [Node("cp-1")]),
            now, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        KubernetesCluster cluster = await dbFactory.Context.KubernetesClusters.SingleAsync();
        await Assert.That(cluster.TenantId).IsEqualTo(tenantId);
        await Assert.That(cluster.CaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That(cluster.Distro).IsEqualTo("unknown");
        await Assert.That(cluster.DisplayName).IsNull();
        await Assert.That(cluster.FirstSeenAt).IsEqualTo(now);
        await Assert.That(cluster.LastSeenAt).IsEqualTo(now);
        await Assert.That(cluster.KubeSystemUid).IsEqualTo(UidA);
        await Assert.That(cluster.ClusterVersion).IsEqualTo("v1.31.0");
        await Assert.That(cluster.RoleOutdated).IsTrue();
        await Assert.That(cluster.Truncated).IsTrue();
        await Assert.That(cluster.ReporterError).IsNull();
        await Assert.That(cluster.LeaseUid).IsEqualTo("lease-x");
        await Assert.That(cluster.LeaseCreatedAt).IsEqualTo(Epoch1);
        await Assert.That(cluster.LeaseTransitions).IsEqualTo(4L);
        await Assert.That(cluster.LastSnapshotReceivedAt).IsEqualTo(T0);
        await Assert.That(cluster.LastSnapshotTelemetryId).IsEqualTo(10L);
        await Assert.That(cluster.ActiveReporterMachineId).IsEqualTo(machineId);

        KubernetesReporterCandidate candidate = (await CandidateOfAsync(dbFactory.Context, machineId))!;
        await Assert.That(candidate.ClusterId).IsEqualTo(cluster.Id);
        await Assert.That(candidate.TenantId).IsEqualTo(tenantId);
        await Assert.That(candidate.LastReportAt).IsEqualTo(T0);
        await Assert.That(candidate.ReporterError).IsNull();
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(10L);
    }

    [Test]
    public async Task Snapshot_ErrorSnapshotMatchingNoCluster_CreatesNothing()
    {
        // Intent: an error snapshot tells nothing about the cluster, so it must never found one.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(10, tenantId, machineId, T0, uid: null, reporterError: "credentials_rejected"));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Unresolved);
        await AssertNothingWrittenAsync(dbFactory.Context);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(10L);
    }

    [Test]
    public async Task Snapshot_WithoutAFingerprintMatchingNoCluster_CreatesNothing()
    {
        // Intent: a cluster is keyed by its CA, so a snapshot that could not read it cannot found one.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(10, tenantId, machineId, T0, fingerprint: null, nodes: [Node("cp-1")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Unresolved);
        await AssertNothingWrittenAsync(dbFactory.Context);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(10L);
    }

    [Test]
    public async Task Snapshot_ErrorSnapshotWithFingerprintOfOneUidlessCluster_AdoptsItAndRecordsTheError()
    {
        // Intent: adoption is not creation. An error snapshot whose CA singles out one UID-less cluster
        // still lands on it, so the cluster page can show why reporting is failing.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(10, tenantId, machineId, T0, uid: null, reporterError: "api_unreachable"));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.ErrorRecorded);
        await Assert.That((await ClusterAsync(dbFactory.Context, clusterId)).ReporterError).IsEqualTo("api_unreachable");
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task Snapshot_MachineIsDeletedUnknownOrFromAnotherTenant_ChangesNothingButTheCursor()
    {
        // Intent: a snapshot from a machine the tenant does not have is acknowledged and dropped, with
        // the cursor moving past it so it is not read again.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long deleted = await SeedMachineAsync(dbFactory.Context, tenantId, isDeleted: true);
        long foreign = await SeedMachineAsync(dbFactory.Context, otherTenantId);

        SnapshotOutcome forDeleted = await ApplyAsync(repo, Snapshot(10, tenantId, deleted, T0));
        SnapshotOutcome forForeign = await ApplyAsync(repo, Snapshot(11, tenantId, foreign, T0));
        SnapshotOutcome forUnknown = await ApplyAsync(repo, Snapshot(12, tenantId, 987654, T0));

        await Assert.That(forDeleted).IsEqualTo(SnapshotOutcome.MachineDeleted);
        await Assert.That(forForeign).IsEqualTo(SnapshotOutcome.MachineDeleted);
        await Assert.That(forUnknown).IsEqualTo(SnapshotOutcome.MachineDeleted);
        await AssertNothingWrittenAsync(dbFactory.Context);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(12L);
    }

    [Test]
    public async Task Snapshot_UidDiffersFromTheSendersCluster_MovesTheCandidateAndListedNodesToTheClusterOfItsUid()
    {
        // Intent: two clusters signed by one CA were grouped together before any snapshot could tell them
        // apart. A snapshot whose UID differs from the cluster the sender was grouped into belongs to the
        // other cluster: it never overwrites the first cluster's UID, and instead takes the sender's
        // candidate row and the nodes it lists to a cluster of its own, leaving the rest where they are.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long first = await SeedClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, uid: UidB, displayName: "one",
            leaseCreatedAt: Epoch1, leaseTransitions: 3, lastSnapshotAt: T0.AddHours(-1), lastSnapshotId: 5, activeReporter: 987);
        await SeedCandidateAsync(dbFactory.Context, tenantId, first, machineId, T0.AddMinutes(-20));
        long nodeA = await SeedNodeAsync(dbFactory.Context, tenantId, first, "a", machineId, lastLocalReportAt: T0.AddMinutes(-10));
        long nodeB = await SeedNodeAsync(dbFactory.Context, tenantId, first, "b", lastReporterSeenAt: T0.AddHours(-1));
        long nodeC = await SeedNodeAsync(dbFactory.Context, tenantId, first, "c", lastReporterSeenAt: T0.AddHours(-1));
        string before = await StateOfAsync(dbFactory.Context, first);

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(10, tenantId, machineId, T0, uid: UidA, nodes: [Node("a"), Node("b")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        await Assert.That(await StateOfAsync(dbFactory.Context, first)).IsEqualTo(before);
        KubernetesCluster created = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id != first);
        await Assert.That(created.KubeSystemUid).IsEqualTo(UidA);
        await Assert.That(created.CaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That(created.ActiveReporterMachineId).IsEqualTo(machineId);
        await Assert.That(created.LeaseTransitions).IsEqualTo(1L);
        await Assert.That((await CandidateOfAsync(dbFactory.Context, machineId))!.ClusterId).IsEqualTo(created.Id);

        Dictionary<string, KubernetesNode> moved = await NodesOfAsync(dbFactory.Context, created.Id);
        await Assert.That(moved.Keys).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(moved["a"].Id).IsEqualTo(nodeA);
        await Assert.That(moved["a"].MachineId).IsEqualTo(machineId);
        await Assert.That(moved["b"].Id).IsEqualTo(nodeB);
        Dictionary<string, KubernetesNode> kept = await NodesOfAsync(dbFactory.Context, first);
        await Assert.That(kept.Keys).IsEquivalentTo(new[] { "c" });
        await Assert.That(kept["c"].Id).IsEqualTo(nodeC);
    }

    [Test]
    public async Task Snapshot_UidDiffersAndTheClusterOfItsUidExists_MovesIntoItAndCombinesTheSameNamedNodes()
    {
        // Intent: when the snapshot's UID already has a cluster, the sender and the nodes it lists move
        // there. A node of the same name that the target already has is combined with the moved one, and
        // neither the machine link nor the node name collides.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long old = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidB);
        long target = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);
        await SeedCandidateAsync(dbFactory.Context, tenantId, old, machineId, T0.AddMinutes(-20));
        await SeedNodeAsync(dbFactory.Context, tenantId, old, "a", machineId, lastLocalReportAt: T0.AddMinutes(-10));
        await SeedNodeAsync(dbFactory.Context, tenantId, target, "a", lastReporterSeenAt: T0.AddHours(-2));

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(10, tenantId, machineId, T0, nodes: [Node("a")], truncated: true));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(2);
        Dictionary<string, KubernetesNode> nodes = await NodesOfAsync(dbFactory.Context, target);
        await Assert.That(nodes.Keys).IsEquivalentTo(new[] { "a" });
        await Assert.That(nodes["a"].MachineId).IsEqualTo(machineId);
        await Assert.That(await NodesOfAsync(dbFactory.Context, old)).IsEmpty();
        await Assert.That((await CandidateOfAsync(dbFactory.Context, machineId))!.ClusterId).IsEqualTo(target);
    }

    [Test]
    public async Task Snapshot_SeparatedIntoAClusterThatFencesIt_StillMovesTheCandidateAndTheListedNodes()
    {
        // Intent: separation and merging happen before fencing, because only the surviving cluster can
        // fence the snapshot. A snapshot that is then fenced by the cluster of its own UID has still shown
        // where its sender and the nodes it lists belong, so they move there, and nothing else is written.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long old = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidB);
        long owner = await SeedClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, uid: UidA,
            leaseCreatedAt: Epoch2, leaseTransitions: 4, lastSnapshotAt: T0, lastSnapshotId: 100, activeReporter: 987);
        await SeedCandidateAsync(dbFactory.Context, tenantId, old, machineId, T0.AddMinutes(-20));
        long listedNode = await SeedNodeAsync(dbFactory.Context, tenantId, old, "a", machineId, lastLocalReportAt: T0.AddMinutes(-10));
        long otherNode = await SeedNodeAsync(dbFactory.Context, tenantId, old, "b", lastReporterSeenAt: T0.AddHours(-1));

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(101, tenantId, machineId, T0.AddMinutes(1), transitions: 9, leaseCreatedAt: Epoch1, nodes: [Node("a")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Fenced);
        KubernetesReporterCandidate candidate = (await CandidateOfAsync(dbFactory.Context, machineId))!;
        await Assert.That(candidate.ClusterId).IsEqualTo(owner);
        await Assert.That(candidate.LastReportAt).IsEqualTo(T0.AddMinutes(-20));
        Dictionary<string, KubernetesNode> moved = await NodesOfAsync(dbFactory.Context, owner);
        await Assert.That(moved.Keys).IsEquivalentTo(new[] { "a" });
        await Assert.That(moved["a"].Id).IsEqualTo(listedNode);
        await Assert.That(moved["a"].LastReporterSeenAt).IsNull();
        Dictionary<string, KubernetesNode> kept = await NodesOfAsync(dbFactory.Context, old);
        await Assert.That(kept["b"].Id).IsEqualTo(otherNode);
        await Assert.That((await ClusterAsync(dbFactory.Context, owner)).LeaseTransitions).IsEqualTo(4L);
        await Assert.That((await ClusterAsync(dbFactory.Context, owner)).ActiveReporterMachineId).IsEqualTo(987L);
    }

    [Test]
    public async Task Snapshot_UidDiffersButTheSnapshotCannotFoundACluster_ChangesNothingButTheCursor()
    {
        // Intent: separating needs a cluster of the snapshot's own, and one is only founded from a
        // fingerprint and a snapshot of the cluster. Without either, nothing moves.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long first = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidB);
        await SeedCandidateAsync(dbFactory.Context, tenantId, first, machineId, T0.AddMinutes(-20));
        await SeedNodeAsync(dbFactory.Context, tenantId, first, "a", machineId, lastLocalReportAt: T0.AddMinutes(-10));
        string before = await StateOfAsync(dbFactory.Context, first);

        SnapshotOutcome withoutFingerprint = await ApplyAsync(
            repo, Snapshot(10, tenantId, machineId, T0, fingerprint: null, nodes: [Node("a")]));
        SnapshotOutcome withError = await ApplyAsync(
            repo, Snapshot(11, tenantId, machineId, T0.AddMinutes(1), reporterError: "api_unreachable"));

        await Assert.That(withoutFingerprint).IsEqualTo(SnapshotOutcome.Unresolved);
        await Assert.That(withError).IsEqualTo(SnapshotOutcome.Unresolved);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
        await Assert.That(await StateOfAsync(dbFactory.Context, first)).IsEqualTo(before);
        await Assert.That((await CandidateOfAsync(dbFactory.Context, machineId))!.ClusterId).IsEqualTo(first);
        await Assert.That((await NodesOfAsync(dbFactory.Context, first)).Keys).IsEquivalentTo(new[] { "a" });
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(11L);
    }

    [Test]
    public async Task Snapshot_ReEnabledAfterUninstall_NewLeaseEpochRestartsTheTransitionCount()
    {
        // Intent: uninstall deletes the Lease, so a re-enabled cluster's transition count restarts at
        // zero. A snapshot from the new Lease epoch must be accepted although its count (1) is below the
        // count (7) the old epoch reached, and even though it is older by receipt time and id.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, uid: UidA,
            leaseCreatedAt: Epoch1, leaseTransitions: 7, lastSnapshotAt: T0, lastSnapshotId: 100, activeReporter: machineId);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, machineId, T0);

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(50, tenantId, machineId, T0.AddMinutes(-1), transitions: 1, leaseCreatedAt: Epoch2, leaseUid: "lease-new"));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        KubernetesCluster cluster = await ClusterAsync(dbFactory.Context, clusterId);
        await Assert.That(cluster.LeaseCreatedAt).IsEqualTo(Epoch2);
        await Assert.That(cluster.LeaseUid).IsEqualTo("lease-new");
        await Assert.That(cluster.LeaseTransitions).IsEqualTo(1L);
        await Assert.That(cluster.LastSnapshotReceivedAt).IsEqualTo(T0.AddMinutes(-1));
        await Assert.That(cluster.LastSnapshotTelemetryId).IsEqualTo(50L);
    }

    [Test]
    public async Task Snapshot_FromAnOlderLeaseEpoch_IsFencedWhateverItsTransitionCount()
    {
        // Intent: a snapshot taken under a Lease that has since been deleted and recreated is stale, even
        // when its transition count is far higher than the new Lease's.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, uid: UidA,
            leaseCreatedAt: Epoch1, leaseTransitions: 2, lastSnapshotAt: T0, lastSnapshotId: 100, activeReporter: machineId);
        string before = await StateOfAsync(dbFactory.Context, clusterId);

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(200, tenantId, machineId, T0.AddHours(1), transitions: 99, leaseCreatedAt: Epoch0, nodes: [Node("late")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Fenced);
        await Assert.That(await StateOfAsync(dbFactory.Context, clusterId)).IsEqualTo(before);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(0);
        await Assert.That(await dbFactory.Context.KubernetesReporterCandidates.CountAsync()).IsEqualTo(0);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(200L);
    }

    [Test]
    [Arguments(6L, 600, 101L, SnapshotOutcome.Fenced)]
    [Arguments(7L, -1, 99L, SnapshotOutcome.Fenced)]
    [Arguments(7L, 0, 99L, SnapshotOutcome.Fenced)]
    [Arguments(7L, 0, 100L, SnapshotOutcome.Fenced)]
    [Arguments(7L, 0, 101L, SnapshotOutcome.Accepted)]
    [Arguments(7L, 1, 50L, SnapshotOutcome.Accepted)]
    [Arguments(8L, -3600, 1L, SnapshotOutcome.Accepted)]
    public async Task Snapshot_WithinOneLeaseEpoch_IsOrderedByTransitionsThenReceiptTimeThenId(
        long transitions, int secondsAfterLastAccepted, long telemetryId, SnapshotOutcome expected)
    {
        // Intent: within one Lease epoch a lower transition count is stale, and an equal one is accepted
        // only when (receipt time, telemetry id) is strictly newer than the last accepted snapshot's.
        // An equal receipt time is broken by the id, and a higher count wins whatever its receipt time.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, uid: UidA,
            leaseCreatedAt: Epoch1, leaseTransitions: 7, lastSnapshotAt: T0, lastSnapshotId: 100, activeReporter: 987);

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(telemetryId, tenantId, machineId, T0.AddSeconds(secondsAfterLastAccepted), transitions: transitions));

        await Assert.That(outcome).IsEqualTo(expected);
        KubernetesCluster cluster = await ClusterAsync(dbFactory.Context, clusterId);
        long expectedActive = (expected == SnapshotOutcome.Accepted) ? machineId : 987;
        await Assert.That(cluster.ActiveReporterMachineId).IsEqualTo(expectedActive);
        long expectedTransitions = (expected == SnapshotOutcome.Accepted) ? transitions : 7L;
        await Assert.That(cluster.LeaseTransitions).IsEqualTo(expectedTransitions);
    }

    [Test]
    public async Task Snapshot_FromAStaleHolderAfterFailover_IsIgnored()
    {
        // Intent: holder A is replaced by holder B, whose Lease transition count is higher. A snapshot of A
        // that was still queued and arrives afterwards must not take the cluster back or alter its nodes.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long holderA = await SeedMachineAsync(dbFactory.Context, tenantId);
        long holderB = await SeedMachineAsync(dbFactory.Context, tenantId);

        await ApplyAsync(repo, Snapshot(10, tenantId, holderA, T0, transitions: 5, nodes: [Node("n1"), Node("n2")]));
        await ApplyAsync(repo, Snapshot(11, tenantId, holderB, T0.AddMinutes(10), transitions: 6, nodes: [Node("n1")]));
        SnapshotOutcome stale = await ApplyAsync(
            repo, Snapshot(12, tenantId, holderA, T0.AddMinutes(11), transitions: 5, nodes: [Node("n1"), Node("n2"), Node("n3")]));

        await Assert.That(stale).IsEqualTo(SnapshotOutcome.Fenced);
        KubernetesCluster cluster = await dbFactory.Context.KubernetesClusters.SingleAsync();
        await Assert.That(cluster.ActiveReporterMachineId).IsEqualTo(holderB);
        await Assert.That(cluster.LeaseTransitions).IsEqualTo(6L);
        await Assert.That((await NodesOfAsync(dbFactory.Context, cluster.Id)).Keys).IsEquivalentTo(new[] { "n1" });
    }

    [Test]
    public async Task Snapshot_Replayed_IsFencedAndChangesNothing()
    {
        // Intent: applying the same snapshot twice, as a retried projection row would, is idempotent: the
        // second is not newer than itself.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        ClusterSnapshot snapshot = Snapshot(10, tenantId, machineId, T0, nodes: [Node("n1")]);
        await ApplyAsync(repo, snapshot);
        long clusterId = (await dbFactory.Context.KubernetesClusters.SingleAsync()).Id;
        string before = await StateOfAsync(dbFactory.Context, clusterId);

        SnapshotOutcome second = await ApplyAsync(repo, snapshot);

        await Assert.That(second).IsEqualTo(SnapshotOutcome.Fenced);
        await Assert.That(await StateOfAsync(dbFactory.Context, clusterId)).IsEqualTo(before);
    }

    [Test]
    public async Task Snapshot_Fenced_WritesNoCandidateAndNoNodes()
    {
        // Intent: a fenced snapshot moves the cursor and nothing else, so it neither registers its sender
        // as a candidate nor touches the nodes the cluster currently holds.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, uid: UidA,
            leaseCreatedAt: Epoch1, leaseTransitions: 9, lastSnapshotAt: T0, lastSnapshotId: 100, activeReporter: 987);
        long nodeId = await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "kept", lastLocalReportAt: T0.AddDays(-5));

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(101, tenantId, sender, T0.AddMinutes(1), transitions: 8, nodes: [Node("new")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Fenced);
        await Assert.That(await CandidateOfAsync(dbFactory.Context, sender)).IsNull();
        Dictionary<string, KubernetesNode> nodes = await NodesOfAsync(dbFactory.Context, clusterId);
        await Assert.That(nodes.Keys).IsEquivalentTo(new[] { "kept" });
        await Assert.That(nodes["kept"].Id).IsEqualTo(nodeId);
        await Assert.That(nodes["kept"].LastReporterSeenAt).IsNull();
    }

    [Test]
    public async Task Snapshot_FencedAgainstTheSurvivorOfAUidMerge_NotAgainstTheClusterItWasResolvedTo()
    {
        // Intent: order matters. The sender is a candidate of a cluster that has no Lease state yet, while
        // the cluster holding the snapshot's UID has accepted a newer Lease epoch. The two are one
        // cluster, so the snapshot must be fenced by the merged cluster's epoch, not accepted because the
        // cluster it was first resolved to knows no better.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long grouped = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long holder = await SeedClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, uid: UidA,
            leaseCreatedAt: Epoch2, leaseTransitions: 2, lastSnapshotAt: T0, lastSnapshotId: 100, activeReporter: 987);
        await SeedCandidateAsync(dbFactory.Context, tenantId, grouped, sender, T0.AddMinutes(-30));

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(101, tenantId, sender, T0.AddMinutes(1), transitions: 9, leaseCreatedAt: Epoch1, nodes: [Node("late")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Fenced);
        KubernetesCluster survivor = await dbFactory.Context.KubernetesClusters.SingleAsync();
        await Assert.That(survivor.Id).IsEqualTo(grouped);
        await Assert.That(survivor.KubeSystemUid).IsEqualTo(UidA);
        await Assert.That(survivor.LeaseCreatedAt).IsEqualTo(Epoch2);
        await Assert.That(survivor.LeaseTransitions).IsEqualTo(2L);
        await Assert.That(survivor.LastSnapshotTelemetryId).IsEqualTo(100L);
        await Assert.That(survivor.ActiveReporterMachineId).IsEqualTo(987L);
        await Assert.That((await CandidateOfAsync(dbFactory.Context, sender))!.ClusterId).IsEqualTo(grouped);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(0);
        await Assert.That(holder).IsGreaterThan(grouped);
    }

    private static DateTimeOffset EpochOf(int index)
    {
        return index switch
        {
            0 => Epoch0,
            1 => Epoch1,
            _ => Epoch2,
        };
    }

    [Test]
    [Arguments(1, 4L, -60, 20L, 2, 1L, -120, 10L, true)]
    [Arguments(2, 6L, -60, 20L, 1, 9L, -30, 90L, false)]
    [Arguments(1, 4L, -60, 20L, 1, 6L, -120, 10L, true)]
    [Arguments(1, 6L, -60, 20L, 1, 4L, -30, 90L, false)]
    [Arguments(1, 4L, -60, 20L, 1, 4L, -30, 5L, true)]
    [Arguments(1, 4L, -30, 20L, 1, 4L, -60, 90L, false)]
    [Arguments(1, 4L, -30, 20L, 1, 4L, -30, 21L, true)]
    [Arguments(1, 4L, -30, 21L, 1, 4L, -30, 20L, false)]
    public async Task Snapshot_UidMerge_TheSurvivorKeepsTheNewerLeaseStateOfTheTwoClusters(
        int survivorEpoch, long survivorTransitions, int survivorMinutes, long survivorMarker,
        int otherEpoch, long otherTransitions, int otherMinutes, long otherMarker, bool otherWins)
    {
        // Intent: when two clusters merge, the survivor keeps the newer Lease epoch of the two, and within
        // one epoch the larger transition count, then the newer last-accepted (time, id), together with
        // the reporter that sent that snapshot. A snapshot from an epoch older than both is then fenced,
        // which shows the merged state without anything else being written on top of it.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long survivorId = await SeedClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, displayName: "keeps",
            leaseCreatedAt: EpochOf(survivorEpoch), leaseTransitions: survivorTransitions,
            lastSnapshotAt: T0.AddMinutes(survivorMinutes), lastSnapshotId: survivorMarker, activeReporter: 301);
        await SeedClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, uid: UidA,
            leaseCreatedAt: EpochOf(otherEpoch), leaseTransitions: otherTransitions,
            lastSnapshotAt: T0.AddMinutes(otherMinutes), lastSnapshotId: otherMarker, activeReporter: 302);
        await SeedCandidateAsync(dbFactory.Context, tenantId, survivorId, sender, T0.AddMinutes(-30));

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(500, tenantId, sender, T0.AddHours(1), transitions: 99, leaseCreatedAt: Epoch0));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Fenced);
        KubernetesCluster survivor = await dbFactory.Context.KubernetesClusters.SingleAsync();
        await Assert.That(survivor.Id).IsEqualTo(survivorId);
        await Assert.That(survivor.DisplayName).IsEqualTo("keeps");
        await Assert.That(survivor.KubeSystemUid).IsEqualTo(UidA);
        await Assert.That(survivor.LeaseCreatedAt).IsEqualTo(EpochOf(otherWins ? otherEpoch : survivorEpoch));
        await Assert.That(survivor.LeaseTransitions).IsEqualTo(otherWins ? otherTransitions : survivorTransitions);
        await Assert.That(survivor.LastSnapshotReceivedAt).IsEqualTo(T0.AddMinutes(otherWins ? otherMinutes : survivorMinutes));
        await Assert.That(survivor.LastSnapshotTelemetryId).IsEqualTo(otherWins ? otherMarker : survivorMarker);
        await Assert.That(survivor.ActiveReporterMachineId).IsEqualTo(otherWins ? 302L : 301L);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Snapshot_UidMerge_KeepsTheLowerIdAndMovesNodesAndCandidatesIntoIt(bool uidHolderIsOlder)
    {
        // Intent: two clusters of one real cluster, one of which holds the UID, become one. The lower id
        // survives whichever of the two held the UID, takes the UID, and receives the other's nodes and
        // candidates. The partial unique index over (tenant, UID) holds throughout, in either order.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long other = await SeedMachineAsync(dbFactory.Context, tenantId);
        long first;
        long second;
        long uidless;
        long holder;
        if (uidHolderIsOlder)
        {
            holder = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);
            uidless = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
            first = holder;
            second = uidless;
        }
        else
        {
            uidless = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
            holder = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);
            first = uidless;
            second = holder;
        }

        await SeedCandidateAsync(dbFactory.Context, tenantId, uidless, sender, T0.AddMinutes(-30));
        await SeedCandidateAsync(dbFactory.Context, tenantId, holder, other, T0.AddMinutes(-30));
        await SeedNodeAsync(dbFactory.Context, tenantId, uidless, "a", sender, lastLocalReportAt: T0.AddMinutes(-3));
        await SeedNodeAsync(dbFactory.Context, tenantId, holder, "b", other, lastLocalReportAt: T0.AddMinutes(-3));

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("a"), Node("b")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        KubernetesCluster survivor = await dbFactory.Context.KubernetesClusters.SingleAsync();
        await Assert.That(survivor.Id).IsEqualTo(first);
        await Assert.That(survivor.KubeSystemUid).IsEqualTo(UidA);
        await Assert.That((await NodesOfAsync(dbFactory.Context, first)).Keys).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That((await CandidateOfAsync(dbFactory.Context, sender))!.ClusterId).IsEqualTo(first);
        await Assert.That((await CandidateOfAsync(dbFactory.Context, other))!.ClusterId).IsEqualTo(first);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync(c => c.Id == second)).IsEqualTo(0);
    }

    [Test]
    public async Task Snapshot_UidMerge_CombinesSameNamedNodesKeepingTheFreshestFieldsAndOneMachineLink()
    {
        // Intent: a node that both clusters hold becomes one row. Each field comes from the row with the
        // newer local report or reporter sighting, the machine link comes from whichever row has one (the
        // newer local report wins when both do, and the other machine is unlinked so the unique index on
        // the link holds), and the older row disappears.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long machineOld = await SeedMachineAsync(dbFactory.Context, tenantId);
        long machineNew = await SeedMachineAsync(dbFactory.Context, tenantId);
        long survivorCluster = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long mergedCluster = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);
        await SeedCandidateAsync(dbFactory.Context, tenantId, survivorCluster, sender, T0.AddMinutes(-30));
        await dbFactory.Context.InsertAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = survivorCluster,
            NodeName = "n",
            MachineId = machineOld,
            LastLocalReportAt = T0.AddHours(-3),
            ReportedCaFingerprint = FingerprintB,
            KubeletVersion = "v1.28.0",
            LocalControlPlane = false,
            LastReporterSeenAt = T0.AddHours(-2),
            Roles = "[\"old\"]",
            ReadyStatus = "False",
            PodCount = 1,
        });
        await dbFactory.Context.InsertAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = mergedCluster,
            NodeName = "n",
            MachineId = machineNew,
            LastLocalReportAt = T0.AddMinutes(-10),
            ReportedCaFingerprint = FingerprintA,
            KubeletVersion = "v1.30.4",
            LocalControlPlane = true,
            LastReporterSeenAt = T0.AddHours(-5),
            Roles = "[\"stale\"]",
            ReadyStatus = "Unknown",
            PodCount = 99,
        });

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("z")], truncated: true));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        KubernetesCluster survivor = await dbFactory.Context.KubernetesClusters.SingleAsync();
        await Assert.That(survivor.Id).IsEqualTo(survivorCluster);
        KubernetesNode merged = (await NodesOfAsync(dbFactory.Context, survivorCluster))["n"];
        await Assert.That(merged.MachineId).IsEqualTo(machineNew);
        await Assert.That(merged.LastLocalReportAt).IsEqualTo(T0.AddMinutes(-10));
        await Assert.That(merged.ReportedCaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That(merged.LocalControlPlane).IsTrue();
        await Assert.That(merged.KubeletVersion).IsEqualTo("v1.30.4");
        await Assert.That(merged.LastReporterSeenAt).IsEqualTo(T0.AddHours(-2));
        await Assert.That(merged.Roles).IsEqualTo("[\"old\"]");
        await Assert.That(merged.ReadyStatus).IsEqualTo("False");
        await Assert.That(merged.PodCount).IsEqualTo(1);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.NodeName == "n")).IsEqualTo(1);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.MachineId == machineOld)).IsEqualTo(0);
    }

    [Test]
    public async Task Snapshot_UidMerge_KeepsTheMachineLinkOfWhicheverSameNamedRowHasOne()
    {
        // Intent: when only one of two same-named rows is linked to a machine, the combined row keeps that
        // link even though the other row has the newer local report.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long linkedMachine = await SeedMachineAsync(dbFactory.Context, tenantId);
        long survivorCluster = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long mergedCluster = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);
        await SeedCandidateAsync(dbFactory.Context, tenantId, survivorCluster, sender, T0.AddMinutes(-30));
        await SeedNodeAsync(dbFactory.Context, tenantId, survivorCluster, "n", null, lastLocalReportAt: T0.AddMinutes(-1));
        await SeedNodeAsync(dbFactory.Context, tenantId, mergedCluster, "n", linkedMachine, lastLocalReportAt: T0.AddHours(-4));

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("z")], truncated: true));

        KubernetesNode merged = (await NodesOfAsync(dbFactory.Context, survivorCluster))["n"];
        await Assert.That(merged.MachineId).IsEqualTo(linkedMachine);
        await Assert.That(merged.LastLocalReportAt).IsEqualTo(T0.AddMinutes(-1));
    }

    [Test]
    public async Task Snapshot_OverlapMerge_RejoinsASplitClusterKeepingTheOlderIdAndName()
    {
        // Intent: local reports split one cluster in two before a snapshot could tell. Both are UID-less,
        // share the CA the snapshot reports and share a node name with it. The older cluster survives with
        // its id and name, the duplicate node combines, and the survivor takes the UID.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long machineX = await SeedMachineAsync(dbFactory.Context, tenantId);
        long older = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "prod");
        long younger = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "other name");
        await SeedCandidateAsync(dbFactory.Context, tenantId, younger, sender, T0.AddMinutes(-30));
        await SeedNodeAsync(dbFactory.Context, tenantId, older, "cp-1", machineX, lastLocalReportAt: T0.AddMinutes(-20));
        await SeedNodeAsync(dbFactory.Context, tenantId, younger, "cp-1", null, lastReporterSeenAt: T0.AddHours(-1));
        await SeedNodeAsync(dbFactory.Context, tenantId, younger, "w-1", sender, lastLocalReportAt: T0.AddMinutes(-2));

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("cp-1"), Node("w-1")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        KubernetesCluster survivor = await dbFactory.Context.KubernetesClusters.SingleAsync();
        await Assert.That(survivor.Id).IsEqualTo(older);
        await Assert.That(survivor.DisplayName).IsEqualTo("prod");
        await Assert.That(survivor.KubeSystemUid).IsEqualTo(UidA);
        Dictionary<string, KubernetesNode> nodes = await NodesOfAsync(dbFactory.Context, older);
        await Assert.That(nodes.Keys).IsEquivalentTo(new[] { "cp-1", "w-1" });
        await Assert.That(nodes["cp-1"].MachineId).IsEqualTo(machineX);
        await Assert.That(nodes["w-1"].MachineId).IsEqualTo(sender);
        await Assert.That((await CandidateOfAsync(dbFactory.Context, sender))!.ClusterId).IsEqualTo(older);
    }

    [Test]
    public async Task Snapshot_OverlapMerge_TheSurvivorTakesTheOtherClustersNameWhenItHasNone()
    {
        // Intent: the older cluster's name survives, but an older cluster with no name takes the name an
        // operator gave the cluster that was merged into it.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long older = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long younger = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "named by an operator");
        await SeedCandidateAsync(dbFactory.Context, tenantId, younger, sender, T0.AddMinutes(-30));
        await SeedNodeAsync(dbFactory.Context, tenantId, older, "cp-1", null, lastLocalReportAt: T0.AddMinutes(-20));
        await SeedNodeAsync(dbFactory.Context, tenantId, younger, "w-1", sender, lastLocalReportAt: T0.AddMinutes(-2));

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("cp-1"), Node("w-1")]));

        KubernetesCluster survivor = await dbFactory.Context.KubernetesClusters.SingleAsync();
        await Assert.That(survivor.Id).IsEqualTo(older);
        await Assert.That(survivor.DisplayName).IsEqualTo("named by an operator");
    }

    [Test]
    public async Task Snapshot_OverlapMerge_AClusterWithAnotherCaAndTheSameNodeNameIsNotMerged()
    {
        // Intent: default node names such as ubuntu collide across a tenant's clusters, so a shared name
        // alone never merges. Without a fingerprint relation the clusters stay apart.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long stranger = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintC);
        await SeedNodeAsync(dbFactory.Context, tenantId, stranger, "ubuntu", null, lastLocalReportAt: T0.AddMinutes(-20), reportedFingerprint: FingerprintC);

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("ubuntu")]));

        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(2);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.ClusterId == stranger)).IsEqualTo(1);
        KubernetesCluster own = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id != stranger);
        await Assert.That((await NodesOfAsync(dbFactory.Context, own.Id)).Keys).IsEquivalentTo(new[] { "ubuntu" });
    }

    [Test]
    public async Task Snapshot_OverlapMerge_ClustersSharingTheCaWithDisjointNamesAreNotMerged()
    {
        // Intent: two clusters signed by one CA with different node names are two clusters. The overlap
        // needs a shared node name as well as the fingerprint relation.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long sibling = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long own = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedCandidateAsync(dbFactory.Context, tenantId, own, sender, T0.AddMinutes(-30));
        await SeedNodeAsync(dbFactory.Context, tenantId, sibling, "other-1", null, lastLocalReportAt: T0.AddMinutes(-20));
        await SeedNodeAsync(dbFactory.Context, tenantId, own, "mine-1", sender, lastLocalReportAt: T0.AddMinutes(-20));

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("mine-1")]));

        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(2);
        await Assert.That((await NodesOfAsync(dbFactory.Context, sibling)).Keys).IsEquivalentTo(new[] { "other-1" });
        await Assert.That((await ClusterAsync(dbFactory.Context, sibling)).KubeSystemUid).IsNull();
    }

    [Test]
    [Arguments(30, true)]
    [Arguments(60, true)]
    [Arguments(61, false)]
    public async Task Snapshot_OverlapMerge_ANodeThatReportedTheFingerprintWithinTheHourIsFingerprintRelated(
        int minutesSinceNodeReported, bool merged)
    {
        // Intent: a cluster whose own fingerprint is another one is still related when one of its nodes
        // reported the snapshot's fingerprint within the last hour, which is what a CA rotation in
        // progress looks like. An hour and a minute is too old.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long rotating = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB);
        long own = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedCandidateAsync(dbFactory.Context, tenantId, own, sender, T0.AddMinutes(-30));
        await SeedNodeAsync(
            dbFactory.Context, tenantId, rotating, "cp-1", null,
            lastLocalReportAt: T0.AddMinutes(-minutesSinceNodeReported), reportedFingerprint: FingerprintA);

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("cp-1")]));

        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(merged ? 1 : 2);
        if (merged)
        {
            await Assert.That((await dbFactory.Context.KubernetesClusters.SingleAsync()).Id).IsEqualTo(rotating);
        }
    }

    [Test]
    public async Task Snapshot_OverlapMerge_NeverMergesAClusterThatHasItsOwnUid()
    {
        // Intent: a cluster with a UID of its own is a different cluster by definition, however well its
        // CA and node names overlap the snapshot.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long separate = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidB);
        await SeedNodeAsync(dbFactory.Context, tenantId, separate, "cp-1", null, lastLocalReportAt: T0.AddMinutes(-20));
        string before = await StateOfAsync(dbFactory.Context, separate);

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("cp-1")]));

        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(2);
        await Assert.That(await StateOfAsync(dbFactory.Context, separate)).IsEqualTo(before);
        await Assert.That((await NodesOfAsync(dbFactory.Context, separate)).Keys).IsEquivalentTo(new[] { "cp-1" });
    }

    [Test]
    public async Task Snapshot_OverlapMerge_OfASeparatedClusterJoinsTheOlderSplitHalfWithoutBreakingTheUidIndex()
    {
        // Intent: separation founds a cluster for the snapshot's UID, and an older UID-less cluster that
        // overlaps it is then merged in. The older id survives, so the founded cluster is the one deleted
        // and the UID moves to the survivor after that, never held by two rows at once.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long uidless = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "second cluster");
        long firstUid = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidB);
        await SeedCandidateAsync(dbFactory.Context, tenantId, firstUid, sender, T0.AddMinutes(-30));
        await SeedNodeAsync(dbFactory.Context, tenantId, uidless, "m-1", null, lastLocalReportAt: T0.AddMinutes(-20));
        await SeedNodeAsync(dbFactory.Context, tenantId, firstUid, "m-2", sender, lastLocalReportAt: T0.AddMinutes(-20));

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(10, tenantId, sender, T0, uid: UidA, nodes: [Node("m-1"), Node("m-2")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        List<KubernetesCluster> clusters = await dbFactory.Context.KubernetesClusters.OrderBy(c => c.Id).ToListAsync();
        await Assert.That(clusters.Select(c => c.Id)).IsEquivalentTo(new[] { uidless, firstUid }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(clusters[0].KubeSystemUid).IsEqualTo(UidA);
        await Assert.That(clusters[0].DisplayName).IsEqualTo("second cluster");
        await Assert.That(clusters[1].KubeSystemUid).IsEqualTo(UidB);
        await Assert.That((await NodesOfAsync(dbFactory.Context, uidless)).Keys).IsEquivalentTo(new[] { "m-1", "m-2" });
        await Assert.That((await NodesOfAsync(dbFactory.Context, firstUid)).Count).IsEqualTo(0);
        await Assert.That((await CandidateOfAsync(dbFactory.Context, sender))!.ClusterId).IsEqualTo(uidless);
    }

    [Test]
    public async Task Snapshot_ErrorSnapshot_RecordsTheErrorOnTheClusterAndCandidateAndChangesNoNodes()
    {
        // Intent: an error snapshot says why reporting fails, not what the cluster looks like. It records
        // the error on the cluster and on the sender's candidate row, accepts the Lease state, and leaves
        // every node and the last good cluster details alone, even nodes it would otherwise have removed.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, uid: UidA,
            leaseCreatedAt: Epoch1, leaseTransitions: 2, lastSnapshotAt: T0.AddMinutes(-30), lastSnapshotId: 5, activeReporter: 987);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, sender, T0.AddMinutes(-30));
        long nodeId = await SeedNodeAsync(
            dbFactory.Context, tenantId, clusterId, "cp-1", null, lastReporterSeenAt: T0.AddMinutes(-30), roles: "[\"control-plane\"]");

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(10, tenantId, sender, T0, uid: null, transitions: 3, reporterError: "tls_ca_changed", clusterVersion: null));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.ErrorRecorded);
        KubernetesCluster cluster = await ClusterAsync(dbFactory.Context, clusterId);
        await Assert.That(cluster.ReporterError).IsEqualTo("tls_ca_changed");
        await Assert.That(cluster.ClusterVersion).IsEqualTo("v1.29.0");
        await Assert.That(cluster.KubeSystemUid).IsEqualTo(UidA);
        await Assert.That(cluster.LeaseTransitions).IsEqualTo(3L);
        await Assert.That(cluster.LastSnapshotTelemetryId).IsEqualTo(10L);
        await Assert.That(cluster.ActiveReporterMachineId).IsEqualTo(sender);
        KubernetesReporterCandidate candidate = (await CandidateOfAsync(dbFactory.Context, sender))!;
        await Assert.That(candidate.ReporterError).IsEqualTo("tls_ca_changed");
        await Assert.That(candidate.LastReportAt).IsEqualTo(T0);
        Dictionary<string, KubernetesNode> nodes = await NodesOfAsync(dbFactory.Context, clusterId);
        await Assert.That(nodes.Keys).IsEquivalentTo(new[] { "cp-1" });
        await Assert.That(nodes["cp-1"].Id).IsEqualTo(nodeId);
        await Assert.That(nodes["cp-1"].LastReporterSeenAt).IsEqualTo(T0.AddMinutes(-30));
        await Assert.That(nodes["cp-1"].Roles).IsEqualTo("[\"control-plane\"]");
    }

    [Test]
    public async Task Snapshot_NormalSnapshotAfterAnError_ClearsTheErrorOnTheClusterAndTheCandidate()
    {
        // Intent: the error is cleared by the next good snapshot, which also brings the cluster details up
        // to date.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, uid: UidA, nodes: [Node("cp-1")]));
        await ApplyAsync(repo, Snapshot(11, tenantId, sender, T0.AddMinutes(15), reporterError: "credentials_rejected"));

        SnapshotOutcome outcome = await ApplyAsync(
            repo, Snapshot(12, tenantId, sender, T0.AddMinutes(30), clusterVersion: "v1.31.2", roleOutdated: true, truncated: true, nodes: [Node("cp-1")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        KubernetesCluster cluster = await dbFactory.Context.KubernetesClusters.SingleAsync();
        await Assert.That(cluster.ReporterError).IsNull();
        await Assert.That(cluster.ClusterVersion).IsEqualTo("v1.31.2");
        await Assert.That(cluster.RoleOutdated).IsTrue();
        await Assert.That(cluster.Truncated).IsTrue();
        await Assert.That((await CandidateOfAsync(dbFactory.Context, sender))!.ReporterError).IsNull();
    }

    [Test]
    public async Task Snapshot_HolderWithoutACandidateRow_GetsOneFromItsFirstAcceptedSnapshot()
    {
        // Intent: a holder whose local report lags may have no candidate row yet. Its accepted snapshot
        // proves it reports for the cluster, so the row is created with the snapshot's receipt time.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0));

        KubernetesReporterCandidate candidate = (await CandidateOfAsync(dbFactory.Context, sender))!;
        await Assert.That(candidate.ClusterId).IsEqualTo(clusterId);
        await Assert.That(candidate.TenantId).IsEqualTo(tenantId);
        await Assert.That(candidate.LastReportAt).IsEqualTo(T0);
        await Assert.That(await dbFactory.Context.KubernetesReporterCandidates.CountAsync()).IsEqualTo(1);
    }

    [Test]
    [Arguments(-10, 0)]
    [Arguments(10, 10)]
    public async Task Snapshot_CandidateRow_TakesTheSnapshotsTimeOnlyWhenItIsNewer(int storedMinutesFromSnapshot, int expectedMinutesFromSnapshot)
    {
        // Intent: a local report newer than the snapshot has already refreshed the candidate row, and an
        // older stored time would make that newer report count as superseded later. The row therefore
        // only ever moves forward.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, sender, T0.AddMinutes(storedMinutesFromSnapshot));

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0));

        KubernetesReporterCandidate candidate = (await CandidateOfAsync(dbFactory.Context, sender))!;
        await Assert.That(candidate.LastReportAt).IsEqualTo(T0.AddMinutes(expectedMinutesFromSnapshot));
    }

    [Test]
    public async Task Snapshot_AcceptedAfterALocalReport_DoesNotMakeTheNextLocalReportSuperseded()
    {
        // Intent: the candidate row is shared with local reports, which are refused when older than it.
        // The snapshot dates the row by its own receipt time, not by when it was processed, so a local
        // report received just after the snapshot is still applied.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        LocalReportOutcome first = await repo.ApplyLocalNodeReportAsync(
            new LocalNodeReport(1, tenantId, sender, T0, "k3s", "cp-1", true, true, FingerprintA, "v1.30.4+k3s1", "certificate", true, null),
            T0, CancellationToken.None);
        await ApplyAsync(repo, Snapshot(2, tenantId, sender, T0.AddMinutes(1), nodes: [Node("cp-1")]));

        LocalReportOutcome second = await repo.ApplyLocalNodeReportAsync(
            new LocalNodeReport(3, tenantId, sender, T0.AddMinutes(1).AddSeconds(2), "k3s", "cp-1", true, true, FingerprintA, "v1.30.4+k3s1", "certificate", true, null),
            T0.AddMinutes(1).AddSeconds(5), CancellationToken.None);

        await Assert.That(first).IsEqualTo(LocalReportOutcome.ClusterCreated);
        await Assert.That(second).IsNotEqualTo(LocalReportOutcome.Superseded);
        await Assert.That((await CandidateOfAsync(dbFactory.Context, sender))!.LastReportAt).IsEqualTo(T0.AddMinutes(1).AddSeconds(2));
    }

    [Test]
    public async Task Snapshot_UpsertsNodesWithEveryReporterFieldAndKeepsTheLocalOnes()
    {
        // Intent: a listed node that local reports already described takes every field the cluster knows,
        // as JSON for roles and conditions, while the fields only its own machine can report (its machine
        // link, fingerprint and local control-plane flag, and the time of that report) stay as they were.
        // A node the cluster lists for the first time is created without any local fields.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long local = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, sender, T0.AddMinutes(-30));
        long localNodeId = await SeedNodeAsync(
            dbFactory.Context, tenantId, clusterId, "cp-1", local, lastLocalReportAt: T0.AddMinutes(-5),
            reportedFingerprint: FingerprintA, kubeletVersion: "v1.30.0", localControlPlane: true);
        ClusterSnapshotNode listed = new(
            "cp-1", "machine-id-1", ["control-plane", "etcd"], "True", T0.AddHours(-3),
            [new ClusterSnapshotCondition("MemoryPressure", "True", T0.AddMinutes(-40))], true, "v1.31.0", "containerd://1.7.23", 12, 110);

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [listed, Node("w-1", "machine-id-2")]));

        Dictionary<string, KubernetesNode> nodes = await NodesOfAsync(dbFactory.Context, clusterId);
        KubernetesNode updated = nodes["cp-1"];
        await Assert.That(updated.Id).IsEqualTo(localNodeId);
        await Assert.That(updated.NodeMachineId).IsEqualTo("machine-id-1");
        await Assert.That(updated.Roles).IsEqualTo("[\"control-plane\",\"etcd\"]");
        await Assert.That(updated.ReadyStatus).IsEqualTo("True");
        await Assert.That(updated.ReadySince).IsEqualTo(T0.AddHours(-3));
        using JsonDocument conditions = JsonDocument.Parse(updated.Conditions!);
        await Assert.That(conditions.RootElement.GetArrayLength()).IsEqualTo(1);
        await Assert.That(conditions.RootElement[0].GetProperty("type").GetString()).IsEqualTo("MemoryPressure");
        await Assert.That(conditions.RootElement[0].GetProperty("status").GetString()).IsEqualTo("True");
        await Assert.That(conditions.RootElement[0].GetProperty("lastTransitionTime").GetDateTimeOffset()).IsEqualTo(T0.AddMinutes(-40));
        await Assert.That(updated.Unschedulable).IsTrue();
        await Assert.That(updated.ContainerRuntimeVersion).IsEqualTo("containerd://1.7.23");
        await Assert.That(updated.PodCount).IsEqualTo(12);
        await Assert.That(updated.PodCapacity).IsEqualTo(110);
        await Assert.That(updated.LastReporterSeenAt).IsEqualTo(T0);
        await Assert.That(updated.MachineId).IsEqualTo(local);
        await Assert.That(updated.ReportedCaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That(updated.LocalControlPlane).IsTrue();
        await Assert.That(updated.LastLocalReportAt).IsEqualTo(T0.AddMinutes(-5));
        await Assert.That(updated.KubeletVersion).IsEqualTo("v1.30.0");

        KubernetesNode created = nodes["w-1"];
        await Assert.That(created.TenantId).IsEqualTo(tenantId);
        await Assert.That(created.Roles).IsEqualTo("[\"worker\"]");
        await Assert.That(created.Conditions).IsEqualTo("[]");
        await Assert.That(created.KubeletVersion).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(created.LastReporterSeenAt).IsEqualTo(T0);
        await Assert.That(created.LastLocalReportAt).IsNull();
        await Assert.That(created.LocalControlPlane).IsFalse();
        await Assert.That(created.ReportedCaFingerprint).IsNull();
        await Assert.That(created.FingerprintMismatch).IsFalse();
    }

    [Test]
    [Arguments(89, "v1.30.0")]
    [Arguments(90, "v1.30.0")]
    [Arguments(91, "v1.31.0")]
    public async Task Snapshot_KubeletVersion_IsOnlyTakenWhenTheNodeHasNoLocalReportWithin90Minutes(
        int minutesSinceLocalReport, string expectedVersion)
    {
        // Intent: the node's own machine knows its kubelet version best, so a local report within the
        // staleness threshold (90 minutes, boundary included) keeps its version and an older one yields
        // to the cluster's.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);
        await SeedNodeAsync(
            dbFactory.Context, tenantId, clusterId, "cp-1", null, lastLocalReportAt: T0.AddMinutes(-minutesSinceLocalReport), kubeletVersion: "v1.30.0");

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("cp-1", kubeletVersion: "v1.31.0")]));

        await Assert.That((await NodesOfAsync(dbFactory.Context, clusterId))["cp-1"].KubeletVersion).IsEqualTo(expectedVersion);
    }

    [Test]
    public async Task Snapshot_UnschedulableSince_IsSetOnTheFirstCordonedSnapshotKeptAndClearedWhenSchedulable()
    {
        // Intent: Kubernetes records no cordon time, so the server dates it from the first snapshot that
        // shows the node unschedulable, keeps that time while the node stays cordoned, clears it when the
        // node is schedulable again and dates a later cordon afresh.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("w-1")]));
        KubernetesNode afterSchedulable = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await ApplyAsync(repo, Snapshot(11, tenantId, sender, T0.AddMinutes(15), nodes: [Node("w-1", unschedulable: true)]));
        KubernetesNode afterFirstCordon = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await ApplyAsync(repo, Snapshot(12, tenantId, sender, T0.AddMinutes(30), nodes: [Node("w-1", unschedulable: true)]));
        KubernetesNode afterSecondCordon = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await ApplyAsync(repo, Snapshot(13, tenantId, sender, T0.AddMinutes(45), nodes: [Node("w-1")]));
        KubernetesNode afterUncordon = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await ApplyAsync(repo, Snapshot(14, tenantId, sender, T0.AddMinutes(60), nodes: [Node("w-1", unschedulable: true)]));
        KubernetesNode afterRecordon = await dbFactory.Context.KubernetesNodes.SingleAsync();

        await Assert.That(afterSchedulable.Unschedulable).IsFalse();
        await Assert.That(afterSchedulable.UnschedulableSince).IsNull();
        await Assert.That(afterFirstCordon.Unschedulable).IsTrue();
        await Assert.That(afterFirstCordon.UnschedulableSince).IsEqualTo(T0.AddMinutes(15));
        await Assert.That(afterSecondCordon.UnschedulableSince).IsEqualTo(T0.AddMinutes(15));
        await Assert.That(afterUncordon.Unschedulable).IsFalse();
        await Assert.That(afterUncordon.UnschedulableSince).IsNull();
        await Assert.That(afterRecordon.UnschedulableSince).IsEqualTo(T0.AddMinutes(60));
    }

    [Test]
    public async Task Snapshot_UnschedulableSince_IsDatedByTheSnapshotThatFirstListsACordonedNode()
    {
        // Intent: a node that is already cordoned the first time the cluster lists it is dated by that
        // snapshot, since it is the earliest evidence there is.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("w-1", unschedulable: true), Node("w-2")]));

        List<KubernetesNode> nodes = await dbFactory.Context.KubernetesNodes.OrderBy(n => n.NodeName).ToListAsync();
        await Assert.That(nodes[0].Unschedulable).IsTrue();
        await Assert.That(nodes[0].UnschedulableSince).IsEqualTo(T0);
        await Assert.That(nodes[1].Unschedulable).IsFalse();
        await Assert.That(nodes[1].UnschedulableSince).IsNull();
    }

    [Test]
    public async Task Snapshot_CaFingerprint_IsTakenFromTheSnapshotAndNodeMismatchFlagsAreRecomputed()
    {
        // Intent: the holder sees the cluster's real CA, so the snapshot sets it as the cluster's
        // fingerprint, and every node's mismatch flag is recomputed against it: nodes that reported the new
        // CA clear, nodes still reporting the old one are flagged, and nodes that never reported a
        // fingerprint stay unflagged.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, uid: UidA);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, sender, T0.AddMinutes(-30));
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "rotated", null, lastLocalReportAt: T0.AddMinutes(-5), reportedFingerprint: FingerprintA);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "lagging", null, lastLocalReportAt: T0.AddMinutes(-5), reportedFingerprint: FingerprintB);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "silent", null, lastReporterSeenAt: T0.AddMinutes(-5));
        await dbFactory.Context.KubernetesNodes
            .Where(n => n.NodeName == "rotated")
            .Set(n => n.FingerprintMismatch, true)
            .UpdateAsync();

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, fingerprint: FingerprintA, nodes: [Node("rotated"), Node("lagging"), Node("silent")]));

        await Assert.That((await ClusterAsync(dbFactory.Context, clusterId)).CaFingerprint).IsEqualTo(FingerprintA);
        Dictionary<string, KubernetesNode> nodes = await NodesOfAsync(dbFactory.Context, clusterId);
        await Assert.That(nodes["rotated"].FingerprintMismatch).IsFalse();
        await Assert.That(nodes["lagging"].FingerprintMismatch).IsTrue();
        await Assert.That(nodes["silent"].FingerprintMismatch).IsFalse();
    }

    [Test]
    public async Task Snapshot_WithoutAFingerprint_KeepsTheClustersOwn()
    {
        // Intent: a snapshot whose sender could not read its CA says nothing against the stored one.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, uid: UidA);

        SnapshotOutcome outcome = await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, fingerprint: null));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        await Assert.That((await ClusterAsync(dbFactory.Context, clusterId)).CaFingerprint).IsEqualTo(FingerprintB);
    }

    [Test]
    public async Task Snapshot_MachineIdLink_LinksAnUnlinkedNodeToTheOneMachineWithThatSystemIdIgnoringCase()
    {
        // Intent: a node the cluster lists but no agent has reported is matched to an enrolled machine by
        // /etc/machine-id when exactly one live machine of the tenant carries it, compared without regard
        // to case. A deleted machine with the same id does not count against uniqueness.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long match = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: "0123456789ABCDEF0123456789abcdef");
        await SeedMachineAsync(dbFactory.Context, tenantId, isDeleted: true, systemId: "0123456789abcdef0123456789abcdef");

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("w-1", "0123456789abcdef0123456789ABCDEF")]));

        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await Assert.That(node.MachineId).IsEqualTo(match);
        await Assert.That(node.NodeMachineId).IsEqualTo("0123456789abcdef0123456789ABCDEF");
    }

    [Test]
    public async Task Snapshot_MachineIdLink_AnAmbiguousSystemIdLinksNothing()
    {
        // Intent: cloned machines share /etc/machine-id, and a link to one of them would be a guess.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        await SeedMachineAsync(dbFactory.Context, tenantId, systemId: "cafe0000cafe0000cafe0000cafe0000");
        await SeedMachineAsync(dbFactory.Context, tenantId, systemId: "CAFE0000CAFE0000CAFE0000CAFE0000");

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("w-1", "cafe0000cafe0000cafe0000cafe0000")]));

        await Assert.That((await dbFactory.Context.KubernetesNodes.SingleAsync()).MachineId).IsNull();
    }

    [Test]
    public async Task Snapshot_MachineIdLink_AnEmptySystemIdLinksNothing()
    {
        // Intent: a machine that never recorded its system id, and a node that reports no machine id, are
        // not evidence of anything.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: "");

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("w-1", ""), Node("w-2", null)]));

        List<KubernetesNode> nodes = await dbFactory.Context.KubernetesNodes.ToListAsync();
        await Assert.That(nodes.Count).IsEqualTo(2);
        await Assert.That(nodes.All(n => n.MachineId == null)).IsTrue();
    }

    [Test]
    public async Task Snapshot_MachineIdLink_AMachineAlreadyLinkedToAnotherNodeIsNotLinkedAgain()
    {
        // Intent: a machine is at most one node. When local reports have linked it elsewhere, for example
        // after it was renamed, the snapshot must not link it to a second node, which the unique index on
        // the link would also refuse.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long machine = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: "beef0000beef0000beef0000beef0000");
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "old-name", machine, lastLocalReportAt: T0.AddMinutes(-2));

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("new-name", "beef0000beef0000beef0000beef0000")]));

        Dictionary<string, KubernetesNode> nodes = await NodesOfAsync(dbFactory.Context, clusterId);
        await Assert.That(nodes["old-name"].MachineId).IsEqualTo(machine);
        await Assert.That(nodes["new-name"].MachineId).IsNull();
    }

    [Test]
    public async Task Snapshot_MachineIdLink_ALinkedNodeKeepsItsMachine()
    {
        // Intent: the machine id link is only for nodes nobody has linked. A node its own machine reported
        // keeps that machine, whatever machine id the cluster lists for it.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long reporter = await SeedMachineAsync(dbFactory.Context, tenantId);
        await SeedMachineAsync(dbFactory.Context, tenantId, systemId: "f00d0000f00d0000f00d0000f00d0000");
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "w-1", reporter, lastLocalReportAt: T0.AddMinutes(-2));

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("w-1", "f00d0000f00d0000f00d0000f00d0000")]));

        await Assert.That((await dbFactory.Context.KubernetesNodes.SingleAsync()).MachineId).IsEqualTo(reporter);
    }

    [Test]
    public async Task Snapshot_MachineIdLink_TwoNodesListedWithOneMachineIdLinkNeither()
    {
        // Intent: when the cluster lists two nodes with one machine id, as clones of a template do while
        // only one of them is enrolled, which node the machine is cannot be told from the id.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        await SeedMachineAsync(dbFactory.Context, tenantId, systemId: "abba0000abba0000abba0000abba0000");

        await ApplyAsync(repo, Snapshot(
            10, tenantId, sender, T0,
            nodes: [Node("clone-a", "abba0000abba0000abba0000abba0000"), Node("clone-b", "abba0000abba0000abba0000abba0000")]));

        List<KubernetesNode> nodes = await dbFactory.Context.KubernetesNodes.ToListAsync();
        await Assert.That(nodes.Count).IsEqualTo(2);
        await Assert.That(nodes.All(n => n.MachineId == null)).IsTrue();
    }

    [Test]
    public async Task Snapshot_MachineIdLink_OnlyCountsMachinesOfTheSnapshotsTenant()
    {
        // Intent: a machine of another tenant with the same system id neither is linked nor makes the
        // tenant's own machine ambiguous.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long own = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: "1234abcd1234abcd1234abcd1234abcd");
        await SeedMachineAsync(dbFactory.Context, otherTenantId, systemId: "1234abcd1234abcd1234abcd1234abcd");
        await SeedMachineAsync(dbFactory.Context, otherTenantId, systemId: "abcd1234abcd1234abcd1234abcd1234");

        await ApplyAsync(repo, Snapshot(
            10, tenantId, sender, T0,
            nodes: [Node("w-1", "1234abcd1234abcd1234abcd1234abcd"), Node("w-2", "abcd1234abcd1234abcd1234abcd1234")]));

        Dictionary<string, KubernetesNode> nodes = await NodesOfAsync(dbFactory.Context, (await dbFactory.Context.KubernetesClusters.SingleAsync()).Id);
        await Assert.That(nodes["w-1"].MachineId).IsEqualTo(own);
        await Assert.That(nodes["w-2"].MachineId).IsNull();
    }

    [Test]
    public async Task Snapshot_NonTruncated_RemovesAbsentNodesWithoutARecentLocalReportAndKeepsTheRest()
    {
        // Intent: a complete snapshot is the cluster's whole node list, so a row it no longer lists is a
        // node that left, unless its own machine has reported within the staleness threshold, which can
        // mean the cluster reporter is the one that is wrong or lagging.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, sender, T0.AddMinutes(-30));
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "listed", null, lastReporterSeenAt: T0.AddHours(-1));
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "reporter-only", null, lastReporterSeenAt: T0.AddHours(-1));
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "stale-local", null, lastLocalReportAt: T0.AddMinutes(-91));
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "boundary-local", null, lastLocalReportAt: T0.AddMinutes(-90));
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "recent-local", null, lastLocalReportAt: T0.AddMinutes(-89));

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("listed")]));

        Dictionary<string, KubernetesNode> nodes = await NodesOfAsync(dbFactory.Context, clusterId);
        await Assert.That(nodes.Keys).IsEquivalentTo(new[] { "listed", "boundary-local", "recent-local" });
    }

    [Test]
    public async Task Snapshot_Truncated_RemovesNothing()
    {
        // Intent: a truncated snapshot lists only some of the cluster's nodes, so absence from it proves
        // nothing.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, uid: UidA);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "reporter-only", null, lastReporterSeenAt: T0.AddHours(-1));
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "stale-local", null, lastLocalReportAt: T0.AddHours(-5));

        await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, truncated: true, nodes: [Node("listed")]));

        await Assert.That((await NodesOfAsync(dbFactory.Context, clusterId)).Keys)
            .IsEquivalentTo(new[] { "listed", "reporter-only", "stale-local" });
    }

    [Test]
    public async Task Snapshot_EveryLookupIsScopedToTheTenant()
    {
        // Intent: another tenant's clusters, nodes, candidates and machines never take part in resolution,
        // merging or linking, and the same UID can be held by one cluster in each tenant.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId);
        long foreignMachine = await SeedMachineAsync(dbFactory.Context, otherTenantId);
        long foreignHolder = await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA, uid: UidA, displayName: "theirs");
        long foreignUidless = await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA, displayName: "theirs too");
        await SeedCandidateAsync(dbFactory.Context, otherTenantId, foreignUidless, foreignMachine, T0.AddMinutes(-30));
        await SeedNodeAsync(dbFactory.Context, otherTenantId, foreignUidless, "cp-1", foreignMachine, lastLocalReportAt: T0.AddMinutes(-20));
        string foreignHolderBefore = await StateOfAsync(dbFactory.Context, foreignHolder);
        string foreignUidlessBefore = await StateOfAsync(dbFactory.Context, foreignUidless);

        SnapshotOutcome outcome = await ApplyAsync(repo, Snapshot(10, tenantId, sender, T0, nodes: [Node("cp-1")]));

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        KubernetesCluster own = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.TenantId == tenantId);
        await Assert.That(own.KubeSystemUid).IsEqualTo(UidA);
        await Assert.That(own.Id).IsNotEqualTo(foreignHolder);
        await Assert.That(await StateOfAsync(dbFactory.Context, foreignHolder)).IsEqualTo(foreignHolderBefore);
        await Assert.That(await StateOfAsync(dbFactory.Context, foreignUidless)).IsEqualTo(foreignUidlessBefore);
        await Assert.That((await NodesOfAsync(dbFactory.Context, foreignUidless)).Keys).IsEquivalentTo(new[] { "cp-1" });
        await Assert.That((await CandidateOfAsync(dbFactory.Context, foreignMachine))!.ClusterId).IsEqualTo(foreignUidless);
        await Assert.That((await NodesOfAsync(dbFactory.Context, own.Id)).Keys).IsEquivalentTo(new[] { "cp-1" });
    }

    [Test]
    [Arguments(SnapshotOutcome.Accepted)]
    [Arguments(SnapshotOutcome.Fenced)]
    [Arguments(SnapshotOutcome.ErrorRecorded)]
    [Arguments(SnapshotOutcome.Unresolved)]
    [Arguments(SnapshotOutcome.MachineDeleted)]
    public async Task Snapshot_EveryOutcome_AdvancesTheCursorToTheSnapshotsRow(SnapshotOutcome expected)
    {
        // Intent: the cursor moves in the same transaction as whatever the snapshot did, whatever that
        // was, so a row is never applied twice and never skipped.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long sender = await SeedMachineAsync(dbFactory.Context, tenantId, isDeleted: expected == SnapshotOutcome.MachineDeleted);
        await SeedClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, uid: UidA, leaseCreatedAt: Epoch1, leaseTransitions: 9, lastSnapshotAt: T0, lastSnapshotId: 100);
        ClusterSnapshot snapshot = expected switch
        {
            SnapshotOutcome.Accepted => Snapshot(321, tenantId, sender, T0.AddMinutes(1), transitions: 10),
            SnapshotOutcome.Fenced => Snapshot(321, tenantId, sender, T0.AddMinutes(1), transitions: 8),
            SnapshotOutcome.ErrorRecorded => Snapshot(321, tenantId, sender, T0.AddMinutes(1), transitions: 10, reporterError: "api_unreachable"),
            SnapshotOutcome.Unresolved => Snapshot(321, tenantId, sender, T0.AddMinutes(1), uid: UidB, fingerprint: FingerprintC, reporterError: "api_unreachable"),
            _ => Snapshot(321, tenantId, sender, T0.AddMinutes(1)),
        };

        SnapshotOutcome outcome = await ApplyAsync(repo, snapshot);

        await Assert.That(outcome).IsEqualTo(expected);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(321L);
    }
}
