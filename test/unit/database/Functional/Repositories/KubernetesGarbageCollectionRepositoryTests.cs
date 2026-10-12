// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for Kubernetes inventory garbage collection with cluster reporting: which nodes,
/// candidates and clusters expire, and when a cluster whose reporter went away reverts to what local
/// reports alone know.
/// </summary>
public class KubernetesGarbageCollectionRepositoryTests : KubernetesInventoryRepositoryTestBase
{
    private static readonly DateTimeOffset GcNow = T0.AddDays(30);

    [Test]
    public async Task CollectGarbage_NodeNeedsBothALocalReportAndASightingToBeStale()
    {
        // Intent: a node is removed only when nobody has described it for seven days, neither its own
        // machine nor the cluster reporter. A node the reporter still sees, or whose machine still
        // reports, survives, so an outage of Vord or a paused holder never deletes agentless nodes.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long machine = await SeedMachineAsync(db, tenantId);
        long clusterId = await SeedReportingClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-20), GcNow.AddMinutes(-10));
        await SeedCandidateAsync(db, tenantId, clusterId, machine, GcNow.AddMinutes(-5));
        DateTimeOffset stale = GcNow.AddDays(-7).AddTicks(-1);
        long agentless = await SeedReporterNodeAsync(db, tenantId, clusterId, "agentless", "a1", GcNow.AddDays(-1));
        long reporterBoundary = await SeedReporterNodeAsync(db, tenantId, clusterId, "reporter-boundary", "a2", GcNow.AddDays(-7));
        long localOnly = await SeedReporterNodeAsync(db, tenantId, clusterId, "local-only", null, null, lastLocalReportAt: GcNow.AddDays(-1));
        long localFreshReporterStale = await SeedReporterNodeAsync(db, tenantId, clusterId, "local-fresh", "a3", stale, lastLocalReportAt: GcNow.AddDays(-1));
        long reporterFreshLocalStale = await SeedReporterNodeAsync(db, tenantId, clusterId, "reporter-fresh", "a4", GcNow.AddDays(-1), lastLocalReportAt: stale);
        long bothStale = await SeedReporterNodeAsync(db, tenantId, clusterId, "both-stale", "a5", stale, lastLocalReportAt: stale);
        long reporterStaleNeverLocal = await SeedReporterNodeAsync(db, tenantId, clusterId, "reporter-stale", "a6", stale);
        long localStaleNeverReporter = await SeedReporterNodeAsync(db, tenantId, clusterId, "local-stale", null, null, lastLocalReportAt: stale);

        int deleted = await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(3);
        List<long> remaining = await db.KubernetesNodes.Select(n => n.Id).OrderBy(id => id).ToListAsync();
        await Assert.That(remaining).IsEquivalentTo(new List<long>
        {
            agentless, reporterBoundary, localOnly, localFreshReporterStale, reporterFreshLocalStale,
        });
        await Assert.That(new[] { bothStale, reporterStaleNeverLocal, localStaleNeverReporter }.Intersect(remaining).Count()).IsEqualTo(0);
    }

    [Test]
    public async Task CollectGarbage_AgentlessNodeSurvivesATwoHourSnapshotGap()
    {
        // Intent: a holder that is paused or a Vord that was down for two hours leaves the snapshot
        // stale, but a stale snapshot alone never deletes anything: the agentless node, the cluster's
        // reporter state and its candidate are all kept.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long machine = await SeedMachineAsync(db, tenantId);
        long clusterId = await SeedReportingClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-2), GcNow.AddHours(-2), machine);
        await SeedCandidateAsync(db, tenantId, clusterId, machine, GcNow.AddHours(-1));
        long agentless = await SeedReporterNodeAsync(db, tenantId, clusterId, "agentless", "a1", GcNow.AddHours(-2));

        int deleted = await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(0);
        KubernetesNode node = await db.KubernetesNodes.SingleAsync(n => n.Id == agentless);
        await Assert.That(node.LastReporterSeenAt).IsEqualTo(GcNow.AddHours(-2));
        await Assert.That(node.Roles).IsNotNull();
        KubernetesCluster cluster = await ClusterAsync(db, clusterId);
        await Assert.That(cluster.KubeSystemUid).IsNotNull();
        await Assert.That(cluster.ActiveReporterMachineId).IsEqualTo(machine);
        await Assert.That(await db.KubernetesReporterCandidates.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task CollectGarbage_DeletesCandidatesWithNoReportForSevenDays()
    {
        // Intent: a machine that has sent no local report for more than seven days is no longer a
        // candidate. A candidate exactly seven days old is kept, and so is a fresh one, even in the
        // same cluster, and another tenant's candidates are judged on their own.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        int otherTenant = await SeedTenantAsync(db);
        long fresh = await SeedMachineAsync(db, tenantId);
        long boundary = await SeedMachineAsync(db, tenantId);
        long stale = await SeedMachineAsync(db, tenantId);
        long otherStale = await SeedMachineAsync(db, otherTenant);
        long clusterId = await SeedReportingClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-20), GcNow.AddMinutes(-5));
        long otherCluster = await SeedReportingClusterAsync(db, otherTenant, FingerprintB, GcNow.AddDays(-20), GcNow.AddMinutes(-5));
        await SeedReporterNodeAsync(db, tenantId, clusterId, "node-1", "a1", GcNow.AddMinutes(-5));
        await SeedReporterNodeAsync(db, otherTenant, otherCluster, "node-1", "a1", GcNow.AddMinutes(-5));
        await SeedCandidateAsync(db, tenantId, clusterId, fresh, GcNow.AddDays(-1));
        await SeedCandidateAsync(db, tenantId, clusterId, boundary, GcNow.AddDays(-7));
        await SeedCandidateAsync(db, tenantId, clusterId, stale, GcNow.AddDays(-7).AddTicks(-1));
        await SeedCandidateAsync(db, otherTenant, otherCluster, otherStale, GcNow.AddDays(-8));

        int deleted = await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(2);
        List<long> remaining = await db.KubernetesReporterCandidates.Select(c => c.MachineId).OrderBy(id => id).ToListAsync();
        await Assert.That(remaining).IsEquivalentTo(new List<long> { fresh, boundary });
    }

    [Test]
    public async Task CollectGarbage_ClusterKeepsItsStateWhileItReceivesSnapshotsEvenWithNoCandidates()
    {
        // Intent: reporting counts as disabled only when no candidate is left and snapshots have
        // stopped. A cluster whose latest snapshot is under 90 minutes old, or exactly 90, is never
        // reverted, even when every candidate's local reports lag and the candidates are gone.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long young = await SeedReportingClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-2), GcNow.AddMinutes(-89));
        long exact = await SeedReportingClusterAsync(db, tenantId, FingerprintB, GcNow.AddDays(-2), GcNow.AddMinutes(-90));
        long youngNode = await SeedReporterNodeAsync(db, tenantId, young, "agentless", "a1", GcNow.AddMinutes(-89));
        long exactNode = await SeedReporterNodeAsync(db, tenantId, exact, "agentless", "a1", GcNow.AddMinutes(-90));

        int deleted = await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(0);
        foreach (long clusterId in new[] { young, exact })
        {
            KubernetesCluster cluster = await ClusterAsync(db, clusterId);
            await Assert.That(cluster.KubeSystemUid).IsNotNull();
            await Assert.That(cluster.LeaseTransitions).IsEqualTo(7L);
            await Assert.That(cluster.LastSnapshotReceivedAt).IsNotNull();
        }

        foreach (long nodeId in new[] { youngNode, exactNode })
        {
            KubernetesNode node = await db.KubernetesNodes.SingleAsync(n => n.Id == nodeId);
            await Assert.That(node.Roles).IsNotNull();
            await Assert.That(node.LastReporterSeenAt).IsNotNull();
        }
    }

    [Test]
    public async Task CollectGarbage_ClusterWithCandidatesIsNeverRevertedHoweverStaleItsSnapshot()
    {
        // Intent: while any machine is still a candidate the cluster can start reporting again, so a
        // long-stale snapshot leaves its state in place.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long machine = await SeedMachineAsync(db, tenantId);
        long clusterId = await SeedReportingClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-2), GcNow.AddDays(-3), machine);
        await SeedCandidateAsync(db, tenantId, clusterId, machine, GcNow.AddHours(-1));
        await SeedReporterNodeAsync(db, tenantId, clusterId, "agentless", "a1", GcNow.AddDays(-3));

        int deleted = await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(0);
        await Assert.That((await ClusterAsync(db, clusterId)).KubeSystemUid).IsNotNull();
        await Assert.That((await db.KubernetesNodes.SingleAsync()).Roles).IsNotNull();
    }

    [Test]
    [Arguments(91, true)]
    [Arguments(90, false)]
    public async Task CollectGarbage_RevertBoundary_IsMoreThanNinetyMinutesWithoutASnapshot(int minutesSinceSnapshot, bool reverted)
    {
        // Intent: the snapshot staleness threshold is 90 minutes, and a snapshot exactly that old still
        // counts as fresh.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long clusterId = await SeedReportingClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-2), GcNow.AddMinutes(-minutesSinceSnapshot));
        await SeedReporterNodeAsync(db, tenantId, clusterId, "kept", "a1", GcNow.AddMinutes(-minutesSinceSnapshot), lastLocalReportAt: GcNow.AddMinutes(-5));

        await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        await Assert.That((await ClusterAsync(db, clusterId)).KubeSystemUid == null).IsEqualTo(reverted);
        await Assert.That((await db.KubernetesNodes.SingleAsync()).Roles == null).IsEqualTo(reverted);
    }

    [Test]
    public async Task CollectGarbage_ReportingDisabled_DeletesReporterOnlyNodesAndClearsTheRestToTheirLocalFields()
    {
        // Intent: once no machine is a candidate and snapshots have stopped, the cluster goes back to
        // what local reports alone know. Nodes that only the reporter ever described are removed,
        // nodes a machine reports keep their local fields and lose every reporter column, and the
        // cluster loses its reporter state and its Lease epoch, with the transition count back to 0.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long machine = await SeedMachineAsync(db, tenantId);
        DateTimeOffset snapshot = GcNow.AddHours(-2);
        long clusterId = await SeedReportingClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-2), snapshot, machine);
        long agentless = await SeedReporterNodeAsync(db, tenantId, clusterId, "agentless", "a1", snapshot);
        long linked = await SeedReporterNodeAsync(
            db, tenantId, clusterId, "linked", "a2", snapshot, machineId: machine, lastLocalReportAt: GcNow.AddHours(-1));

        int deleted = await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await db.KubernetesNodes.CountAsync(n => n.Id == agentless)).IsEqualTo(0);

        KubernetesNode node = await db.KubernetesNodes.SingleAsync(n => n.Id == linked);
        await Assert.That(node.NodeMachineId).IsNull();
        await Assert.That(node.Roles).IsNull();
        await Assert.That(node.ReadyStatus).IsNull();
        await Assert.That(node.ReadySince).IsNull();
        await Assert.That(node.Conditions).IsNull();
        await Assert.That(node.Unschedulable).IsFalse();
        await Assert.That(node.UnschedulableSince).IsNull();
        await Assert.That(node.ContainerRuntimeVersion).IsNull();
        await Assert.That(node.PodCount).IsNull();
        await Assert.That(node.PodCapacity).IsNull();
        await Assert.That(node.LastReporterSeenAt).IsNull();
        await Assert.That(node.MachineId).IsEqualTo(machine);
        await Assert.That(node.NodeName).IsEqualTo("linked");
        await Assert.That(node.LastLocalReportAt).IsEqualTo(GcNow.AddHours(-1));
        await Assert.That(node.ReportedCaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That(node.KubeletVersion).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(node.LocalControlPlane).IsTrue();

        KubernetesCluster cluster = await ClusterAsync(db, clusterId);
        await Assert.That(cluster.KubeSystemUid).IsNull();
        await Assert.That(cluster.ClusterVersion).IsNull();
        await Assert.That(cluster.ReporterError).IsNull();
        await Assert.That(cluster.RoleOutdated).IsFalse();
        await Assert.That(cluster.Truncated).IsFalse();
        await Assert.That(cluster.ActiveReporterMachineId).IsNull();
        await Assert.That(cluster.LastSnapshotReceivedAt).IsNull();
        await Assert.That(cluster.LastSnapshotTelemetryId).IsNull();
        await Assert.That(cluster.LeaseUid).IsNull();
        await Assert.That(cluster.LeaseCreatedAt).IsNull();
        await Assert.That(cluster.LeaseTransitions).IsEqualTo(0L);
        await Assert.That(cluster.CaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That(cluster.DisplayName).IsEqualTo("production");
        await Assert.That(cluster.Distro).IsEqualTo("k3s");
        await Assert.That(cluster.FirstSeenAt).IsEqualTo(GcNow.AddDays(-2));
        await Assert.That(cluster.LastSeenAt).IsEqualTo(GcNow.AddDays(-2));
    }

    [Test]
    public async Task CollectGarbage_ReportingDisabled_AfterItsLastCandidateExpires_RevertsInTheSamePass()
    {
        // Intent: the cluster whose last candidate is removed because it went seven days without a local
        // report has no candidate left in the same pass, so it reverts right away if its snapshots have
        // stopped as well.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long machine = await SeedMachineAsync(db, tenantId);
        long clusterId = await SeedReportingClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-20), GcNow.AddDays(-8), machine);
        await SeedCandidateAsync(db, tenantId, clusterId, machine, GcNow.AddDays(-8));
        await SeedReporterNodeAsync(db, tenantId, clusterId, "linked", "a1", GcNow.AddDays(-8), machineId: machine, lastLocalReportAt: GcNow.AddDays(-1));
        await SeedReporterNodeAsync(db, tenantId, clusterId, "agentless", "a2", GcNow.AddDays(-1));

        int deleted = await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(2);
        await Assert.That(await db.KubernetesReporterCandidates.CountAsync()).IsEqualTo(0);
        await Assert.That((await db.KubernetesNodes.SingleAsync()).NodeName).IsEqualTo("linked");
        KubernetesCluster cluster = await ClusterAsync(db, clusterId);
        await Assert.That(cluster.KubeSystemUid).IsNull();
        await Assert.That(cluster.ActiveReporterMachineId).IsNull();
    }

    [Test]
    public async Task CollectGarbage_ReportingDisabledWithoutASnapshotEver_StillRevertsReporterState()
    {
        // Intent: a cluster that kept some reporter state but never had a snapshot accepted, for
        // example one that only ever recorded an error, counts as having no fresh snapshot.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long clusterId = await SeedReportingClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-2), null);
        await SeedReporterNodeAsync(db, tenantId, clusterId, "linked", "a1", GcNow.AddDays(-1), lastLocalReportAt: GcNow.AddHours(-1));

        await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        KubernetesCluster cluster = await ClusterAsync(db, clusterId);
        await Assert.That(cluster.KubeSystemUid).IsNull();
        await Assert.That(cluster.LeaseTransitions).IsEqualTo(0L);
        await Assert.That((await db.KubernetesNodes.SingleAsync()).Roles).IsNull();
    }

    [Test]
    public async Task CollectGarbage_ClusterThatNeverReported_IsLeftExactlyAsItIs()
    {
        // Intent: the revert is only for clusters that have reporter state to undo. A cluster that local
        // reports alone built, with fresh nodes, is untouched.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long machine = await SeedMachineAsync(db, tenantId);
        long clusterId = await SeedClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-2));
        long nodeId = await SeedNodeAsync(db, tenantId, clusterId, "node-1", machine, FingerprintA, GcNow.AddHours(-1));

        int deleted = await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(0);
        KubernetesNode node = await db.KubernetesNodes.SingleAsync(n => n.Id == nodeId);
        await Assert.That(node.MachineId).IsEqualTo(machine);
        await Assert.That(node.LastLocalReportAt).IsEqualTo(GcNow.AddHours(-1));
    }

    [Test]
    public async Task CollectGarbage_ReportingDisabled_OnlyRevertsThatTenantsStaleClusters()
    {
        // Intent: reverting one cluster does not touch another tenant's cluster that is still
        // receiving snapshots.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        int otherTenant = await SeedTenantAsync(db);
        long stale = await SeedReportingClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-2), GcNow.AddHours(-3));
        long live = await SeedReportingClusterAsync(db, otherTenant, FingerprintA, GcNow.AddDays(-2), GcNow.AddMinutes(-5));
        await SeedReporterNodeAsync(db, tenantId, stale, "linked", "a1", GcNow.AddHours(-3), lastLocalReportAt: GcNow.AddHours(-1));
        await SeedReporterNodeAsync(db, otherTenant, live, "agentless", "a2", GcNow.AddMinutes(-5));

        await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        await Assert.That((await ClusterAsync(db, stale)).KubeSystemUid).IsNull();
        await Assert.That((await ClusterAsync(db, live)).KubeSystemUid).IsNotNull();
        await Assert.That((await db.KubernetesNodes.SingleAsync(n => n.TenantId == otherTenant)).Roles).IsNotNull();
    }

    [Test]
    public async Task CollectGarbage_ClusterWithOnlyCandidates_IsKeptAndAClusterWithNothingIsDeleted()
    {
        // Intent: a cluster with a candidate but no node yet, which is a k0s cluster whose only
        // enrolled host has no kubelet, is not empty. One with neither a node nor a candidate is.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long controller = await SeedMachineAsync(db, tenantId);
        long withCandidate = await SeedReportingClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-2), GcNow.AddMinutes(-5), controller);
        await SeedCandidateAsync(db, tenantId, withCandidate, controller, GcNow.AddMinutes(-5));
        long nothing = await SeedClusterAsync(db, tenantId, FingerprintB, GcNow.AddDays(-2));

        int deleted = await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(1);
        List<long> remaining = await db.KubernetesClusters.Select(c => c.Id).ToListAsync();
        await Assert.That(remaining).IsEquivalentTo(new List<long> { withCandidate });
        await Assert.That(remaining.Contains(nothing)).IsFalse();
    }

    [Test]
    public async Task CollectGarbage_ReportingDisabledWithOnlyAgentlessNodes_RemovesTheEmptiedCluster()
    {
        // Intent: when the revert deletes every node of a cluster, nothing is left to keep the cluster,
        // so it goes in the same pass.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long clusterId = await SeedReportingClusterAsync(db, tenantId, FingerprintA, GcNow.AddDays(-2), GcNow.AddHours(-3));
        await SeedReporterNodeAsync(db, tenantId, clusterId, "agentless-1", "a1", GcNow.AddHours(-3));
        await SeedReporterNodeAsync(db, tenantId, clusterId, "agentless-2", "a2", GcNow.AddHours(-3));

        int deleted = await repo.CollectGarbageAsync(GcNow, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(3);
        await Assert.That(await db.KubernetesClusters.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.KubernetesNodes.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task KubernetesReportingPolicy_StaleThreshold_IsNinetyMinutes()
    {
        // Intent: the heartbeat is every 30 minutes, so 90 minutes leaves three missed sends before a
        // cluster is called stale. The value is shared with the readers, so it is pinned.
        await Assert.That(KubernetesReportingPolicy.StaleThreshold).IsEqualTo(TimeSpan.FromMinutes(90));
    }
}
