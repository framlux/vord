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
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for the Kubernetes inventory methods on
/// <see cref="Database.Repositories.DatabaseRepository"/>: how a machine's local report is resolved
/// into a cluster and a linked node, the projection cursor, garbage collection and the telemetry read.
/// </summary>
public class KubernetesInventoryRepositoryTests
{
    private const string FingerprintA = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";
    private const string FingerprintB = "0f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a6978";
    private const string FingerprintC = "c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00";

    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static IKubernetesInventoryRepository CreateRepo(
        TestDatabaseFactory dbFactory, ILogger<Database.Repositories.DatabaseRepository>? logger = null)
    {
        return new Database.Repositories.DatabaseRepository(
            dbFactory.Context, logger ?? NullLogger<Database.Repositories.DatabaseRepository>.Instance);
    }

    private static async Task<int> SeedTenantAsync(DatabaseContext db)
    {
        return await db.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant());
    }

    private static async Task<long> SeedMachineAsync(DatabaseContext db, int tenantId, bool isDeleted = false)
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId);
        machine.IsDeleted = isDeleted;

        return await db.InsertWithInt64IdentityAsync(machine);
    }

    private static async Task<long> SeedClusterAsync(
        DatabaseContext db, int tenantId, string fingerprint, DateTimeOffset seenAt, string distro = "k3s")
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = fingerprint,
            Distro = distro,
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
        });
    }

    private static async Task<long> SeedNodeAsync(
        DatabaseContext db,
        int tenantId,
        long clusterId,
        string nodeName,
        long? machineId,
        string? reportedFingerprint,
        DateTimeOffset? lastLocalReportAt,
        bool mismatch = false)
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = nodeName,
            MachineId = machineId,
            ReportedCaFingerprint = reportedFingerprint,
            FingerprintMismatch = mismatch,
            LastLocalReportAt = lastLocalReportAt,
        });
    }

    private static LocalNodeReport Report(
        long telemetryId,
        int tenantId,
        long machineId,
        DateTimeOffset at,
        string nodeName = "node-1",
        string? fingerprint = FingerprintA,
        bool hasKubelet = true,
        bool controlPlane = false,
        string distro = "k3s",
        string? kubeletVersion = "v1.30.4+k3s1")
    {
        return new LocalNodeReport(
            telemetryId, tenantId, machineId, at, distro, nodeName, hasKubelet, controlPlane, fingerprint, kubeletVersion);
    }

    private static async Task<KubernetesNode> NodeOfMachineAsync(DatabaseContext db, long machineId)
    {
        return await db.KubernetesNodes.SingleAsync(n => n.MachineId == machineId);
    }

    private static async Task<KubernetesCluster> ClusterAsync(DatabaseContext db, long clusterId)
    {
        return await db.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
    }

    private static async Task AssertNothingWrittenAsync(DatabaseContext db)
    {
        await Assert.That(await db.KubernetesClusters.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.KubernetesNodes.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task ApplyLocalReport_FirstReport_CreatesClusterAndLinkedNode()
    {
        // Intent: the first report from a machine with a kubelet and a known CA fingerprint founds a
        // cluster stamped with the report's receipt time and links the machine as a node of it, and the
        // projection cursor lands on the report's telemetry row in the same unit of work.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(42, tenantId, machineId, T0, controlPlane: true), T0.AddSeconds(5), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.ClusterCreated);
        KubernetesCluster cluster = await dbFactory.Context.KubernetesClusters.SingleAsync();
        await Assert.That(cluster.TenantId).IsEqualTo(tenantId);
        await Assert.That(cluster.CaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That(cluster.Distro).IsEqualTo("k3s");
        await Assert.That(cluster.FirstSeenAt).IsEqualTo(T0);
        await Assert.That(cluster.LastSeenAt).IsEqualTo(T0);
        await Assert.That(cluster.DisplayName).IsNull();

        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await Assert.That(node.TenantId).IsEqualTo(tenantId);
        await Assert.That(node.ClusterId).IsEqualTo(cluster.Id);
        await Assert.That(node.NodeName).IsEqualTo("node-1");
        await Assert.That(node.MachineId).IsEqualTo(machineId);
        await Assert.That(node.KubeletVersion).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(node.LocalControlPlane).IsTrue();
        await Assert.That(node.LastLocalReportAt).IsEqualTo(T0);
        await Assert.That(node.ReportedCaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That(node.FingerprintMismatch).IsFalse();

        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(42L);
    }

    [Test]
    public async Task ApplyLocalReport_SecondMachineSameFingerprint_JoinsCluster()
    {
        // Intent: machines are grouped into a cluster by the CA fingerprint they report, so a second
        // machine reporting a fingerprint a cluster already holds becomes another node of that cluster
        // instead of founding its own, and the cluster's last-seen time follows the newest report.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long first = await SeedMachineAsync(dbFactory.Context, tenantId);
        long second = await SeedMachineAsync(dbFactory.Context, tenantId);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, first, T0, nodeName: "node-1"), T0, CancellationToken.None);
        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, second, T0.AddMinutes(3), nodeName: "node-2"), T0.AddMinutes(3), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        KubernetesCluster cluster = await dbFactory.Context.KubernetesClusters.SingleAsync();
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.ClusterId == cluster.Id)).IsEqualTo(2);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, second)).NodeName).IsEqualTo("node-2");
        await Assert.That(cluster.FirstSeenAt).IsEqualTo(T0);
        await Assert.That(cluster.LastSeenAt).IsEqualTo(T0.AddMinutes(3));
    }

    [Test]
    public async Task ApplyLocalReport_EstablishedMembership_FingerprintChanges_FlagsMismatchAndStays()
    {
        // Intent: a machine that already belongs to a cluster is never regrouped because its reported
        // fingerprint changed. While a sibling node still reports the cluster's fingerprint, the cluster
        // keeps its key, the machine is flagged for review, and the flag clears once it reports the
        // original fingerprint again.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long sibling = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, machineId, T0, nodeName: "node-1"), T0, CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(Report(2, tenantId, sibling, T0, nodeName: "node-2"), T0, CancellationToken.None);
        long clusterId = (await dbFactory.Context.KubernetesClusters.SingleAsync()).Id;

        LocalReportOutcome changed = await repo.ApplyLocalNodeReportAsync(
            Report(3, tenantId, machineId, T0.AddMinutes(10), nodeName: "node-1", fingerprint: FingerprintB),
            T0.AddMinutes(10), CancellationToken.None);

        await Assert.That(changed).IsEqualTo(LocalReportOutcome.Grouped);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
        await Assert.That((await ClusterAsync(dbFactory.Context, clusterId)).CaFingerprint).IsEqualTo(FingerprintA);
        KubernetesNode flagged = await NodeOfMachineAsync(dbFactory.Context, machineId);
        await Assert.That(flagged.ClusterId).IsEqualTo(clusterId);
        await Assert.That(flagged.ReportedCaFingerprint).IsEqualTo(FingerprintB);
        await Assert.That(flagged.FingerprintMismatch).IsTrue();

        await repo.ApplyLocalNodeReportAsync(
            Report(4, tenantId, machineId, T0.AddMinutes(20), nodeName: "node-1", fingerprint: FingerprintA),
            T0.AddMinutes(20), CancellationToken.None);

        KubernetesNode cleared = await NodeOfMachineAsync(dbFactory.Context, machineId);
        await Assert.That(cleared.FingerprintMismatch).IsFalse();
        await Assert.That(cleared.ReportedCaFingerprint).IsEqualTo(FingerprintA);
    }

    [Test]
    public async Task ApplyLocalReport_SharedCaWithMatchingNodeName_UsesThatCluster()
    {
        // Intent: when two clusters in a tenant share one CA, a report is placed in the one that
        // already has a node of the reported name, because the node name is the only evidence that
        // separates them.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long other = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long named = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, other, "worker-9", null, FingerprintA, T0);
        long placeholder = await SeedNodeAsync(dbFactory.Context, tenantId, named, "worker-1", null, FingerprintA, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(7, tenantId, machineId, T0.AddMinutes(1), nodeName: "worker-1"), T0.AddMinutes(1), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        KubernetesNode linked = await NodeOfMachineAsync(dbFactory.Context, machineId);
        await Assert.That(linked.Id).IsEqualTo(placeholder);
        await Assert.That(linked.ClusterId).IsEqualTo(named);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(2);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(2);
    }

    [Test]
    public async Task ApplyLocalReport_SharedCaNoNameMatch_IsAmbiguousAndWritesNothing()
    {
        // Intent: with several clusters sharing the CA and none holding a node of the reported name,
        // the report cannot be placed. Guessing would put the machine in the wrong cluster, so only the
        // cursor moves.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long first = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long second = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, first, "worker-8", null, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, second, "worker-9", null, FingerprintA, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(11, tenantId, machineId, T0.AddMinutes(1), nodeName: "worker-1"), T0.AddMinutes(1), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Ambiguous);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(2);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(2);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.MachineId == machineId)).IsEqualTo(0);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(11L);
    }

    [Test]
    public async Task ApplyLocalReport_SharedCaNameInBothClusters_IsAmbiguousAndWritesNothing()
    {
        // Intent: a node name that appears in more than one of the clusters sharing a CA separates
        // nothing, so the report is still ambiguous and is not attached to either.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long first = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long second = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, first, "worker-1", null, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, second, "worker-1", null, FingerprintA, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(12, tenantId, machineId, T0.AddMinutes(1), nodeName: "worker-1"), T0.AddMinutes(1), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Ambiguous);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.MachineId == machineId)).IsEqualTo(0);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(12L);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task ApplyLocalReport_NoFingerprint_NoMembership_WritesNothing(string? fingerprint)
    {
        // Intent: a machine with no established membership and no CA fingerprint has no evidence to be
        // grouped by, so nothing is created for it. Both a missing and an empty fingerprint count as
        // none, and the cursor still advances past the row.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(5, tenantId, machineId, T0, fingerprint: fingerprint), T0, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.NoFingerprint);
        await AssertNothingWrittenAsync(dbFactory.Context);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(5L);
    }

    [Test]
    public async Task ApplyLocalReport_NoFingerprint_EstablishedMembership_StillUpdates()
    {
        // Intent: a machine already in a cluster keeps reporting even when its CA file is momentarily
        // unreadable. The node's version, role and report time are refreshed and the fingerprint it
        // last reported is left as it was.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, machineId, T0), T0, CancellationToken.None);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0.AddMinutes(15), fingerprint: null, controlPlane: true, kubeletVersion: "v1.31.0+k3s1"),
            T0.AddMinutes(15), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        KubernetesNode node = await NodeOfMachineAsync(dbFactory.Context, machineId);
        await Assert.That(node.KubeletVersion).IsEqualTo("v1.31.0+k3s1");
        await Assert.That(node.LocalControlPlane).IsTrue();
        await Assert.That(node.LastLocalReportAt).IsEqualTo(T0.AddMinutes(15));
        await Assert.That(node.ReportedCaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That(node.FingerprintMismatch).IsFalse();
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task ApplyLocalReport_NoKubelet_WritesNothing()
    {
        // Intent: a host that reports no kubelet is not a node, and creating a cluster for it would
        // leave an empty cluster behind, so nothing is written and only the cursor advances.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(9, tenantId, machineId, T0, hasKubelet: false, controlPlane: true), T0, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.NotANode);
        await AssertNothingWrittenAsync(dbFactory.Context);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(9L);
    }

    [Test]
    public async Task ApplyLocalReport_DeletedMachine_WritesNothing()
    {
        // Intent: a soft-deleted machine must not gain or keep a place in the inventory because of a
        // report that was recorded before it was deleted.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, isDeleted: true);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(3, tenantId, machineId, T0), T0, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.MachineDeleted);
        await AssertNothingWrittenAsync(dbFactory.Context);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(3L);
    }

    [Test]
    public async Task ApplyLocalReport_UnknownMachine_WritesNothing()
    {
        // Intent: a report for a machine id that no longer exists, for example after a purge, is
        // treated like a deleted machine rather than failing the projection.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(4, tenantId, 987654, T0), T0, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.MachineDeleted);
        await AssertNothingWrittenAsync(dbFactory.Context);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(4L);
    }

    [Test]
    public async Task ApplyLocalReport_MachineOfAnotherTenant_WritesNothing()
    {
        // Intent: a report is only ever applied to a machine of the tenant the report names, so a
        // mismatched tenant can never attach another tenant's machine to its inventory.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int reportingTenant = await SeedTenantAsync(dbFactory.Context);
        int owningTenant = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, owningTenant);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(6, reportingTenant, machineId, T0), T0, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.MachineDeleted);
        await AssertNothingWrittenAsync(dbFactory.Context);
    }

    [Test]
    public async Task ApplyLocalReport_OlderThanStored_IsSuperseded()
    {
        // Intent: a replayed or reordered report must never overwrite newer state, so a report older
        // than the node's last local report changes nothing except the cursor.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(
            Report(10, tenantId, machineId, T0, kubeletVersion: "v1.31.0+k3s1"), T0, CancellationToken.None);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(11, tenantId, machineId, T0.AddTicks(-1), kubeletVersion: "v1.29.0+k3s1"), T0, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Superseded);
        KubernetesNode node = await NodeOfMachineAsync(dbFactory.Context, machineId);
        await Assert.That(node.KubeletVersion).IsEqualTo("v1.31.0+k3s1");
        await Assert.That(node.LastLocalReportAt).IsEqualTo(T0);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(11L);
    }

    [Test]
    public async Task ApplyLocalReport_SameReceiptTimeAsStored_IsAppliedAgain()
    {
        // Intent: only a strictly older report is superseded. Re-applying the very same report, as
        // happens when a retry replays a row, is idempotent and creates no duplicate cluster or node.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        LocalNodeReport report = Report(10, tenantId, machineId, T0);
        await repo.ApplyLocalNodeReportAsync(report, T0, CancellationToken.None);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(report, T0, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task ApplyLocalReport_OlderThanTheNodeItWouldTake_IsSuperseded()
    {
        // Intent: a machine cannot take a node over from another machine with a report older than the
        // one that machine last sent, so the node stays linked where the newer evidence put it.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long holder = await SeedMachineAsync(dbFactory.Context, tenantId);
        long newcomer = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0.AddMinutes(10));
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "node-1", holder, FingerprintA, T0.AddMinutes(10));

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(8, tenantId, newcomer, T0.AddMinutes(5), nodeName: "node-1"), T0.AddMinutes(5), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Superseded);
        await Assert.That((await dbFactory.Context.KubernetesNodes.SingleAsync()).MachineId).IsEqualTo(holder);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(8L);
    }

    [Test]
    public async Task ApplyLocalReport_NodeRenamed_ReplacesOldRow()
    {
        // Intent: a machine that first reported its hostname and later reports its certificate name is
        // still one node. The old row in the same cluster is removed and the new name is linked, so
        // the cluster never lists the machine twice.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0, nodeName: "host-a"), T0, CancellationToken.None);
        long clusterId = (await dbFactory.Context.KubernetesClusters.SingleAsync()).Id;

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0.AddMinutes(15), nodeName: "node-a.example.com"),
            T0.AddMinutes(15), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await Assert.That(node.NodeName).IsEqualTo("node-a.example.com");
        await Assert.That(node.ClusterId).IsEqualTo(clusterId);
        await Assert.That(node.MachineId).IsEqualTo(machineId);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task ApplyLocalReport_NodeRenamedOntoAnUnlinkedRow_DeletesOldRowAndLinksTheOther()
    {
        // Intent: when the name a machine moves to already exists in its cluster as a node nobody is
        // linked to, that row is adopted and the machine's previous row is removed, so the machine's
        // link moves without ever violating the one-node-per-machine rule.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long previous = await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "host-a", machineId, FingerprintA, T0);
        long adopted = await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "node-a", null, null, null);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0.AddMinutes(15), nodeName: "node-a"), T0.AddMinutes(15), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.Id == previous)).IsEqualTo(0);
        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await Assert.That(node.Id).IsEqualTo(adopted);
        await Assert.That(node.MachineId).IsEqualTo(machineId);
        await Assert.That(node.LastLocalReportAt).IsEqualTo(T0.AddMinutes(15));
    }

    [Test]
    public async Task ApplyLocalReport_RotationCompletes_ReKeysClusterAndClearsFlags()
    {
        // Intent: when every node that has recently reported agrees on a new CA fingerprint, the
        // rotation is finished. The cluster takes the new key instead of leaving all its nodes
        // flagged forever, and every node's flag is recomputed against it.
        using TestDatabaseFactory dbFactory = new();
        CapturingLogger logger = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory, logger);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long first = await SeedMachineAsync(dbFactory.Context, tenantId);
        long second = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, first, T0, nodeName: "node-1"), T0, CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(Report(2, tenantId, second, T0, nodeName: "node-2"), T0, CancellationToken.None);
        long clusterId = (await dbFactory.Context.KubernetesClusters.SingleAsync()).Id;

        await repo.ApplyLocalNodeReportAsync(
            Report(3, tenantId, first, T0.AddMinutes(10), nodeName: "node-1", fingerprint: FingerprintB), T0.AddMinutes(10), CancellationToken.None);
        await Assert.That((await ClusterAsync(dbFactory.Context, clusterId)).CaFingerprint).IsEqualTo(FingerprintA);

        await repo.ApplyLocalNodeReportAsync(
            Report(4, tenantId, second, T0.AddMinutes(20), nodeName: "node-2", fingerprint: FingerprintB), T0.AddMinutes(20), CancellationToken.None);

        await Assert.That((await ClusterAsync(dbFactory.Context, clusterId)).CaFingerprint).IsEqualTo(FingerprintB);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, first)).FingerprintMismatch).IsFalse();
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, second)).FingerprintMismatch).IsFalse();
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
        await Assert.That(logger.Entries.Count(e => (e.Level == LogLevel.Information) && e.Message.Contains("re-keyed"))).IsEqualTo(1);
    }

    [Test]
    public async Task ApplyLocalReport_RotationInProgress_KeepsKeyAndFlags()
    {
        // Intent: while one node reports the new fingerprint and another, within the hour, still
        // reports the old one, the rotation is not finished. The cluster keeps its key and only the
        // node that disagrees with it is flagged.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long first = await SeedMachineAsync(dbFactory.Context, tenantId);
        long second = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, first, T0, nodeName: "node-1"), T0, CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(Report(2, tenantId, second, T0, nodeName: "node-2"), T0, CancellationToken.None);
        long clusterId = (await dbFactory.Context.KubernetesClusters.SingleAsync()).Id;

        await repo.ApplyLocalNodeReportAsync(
            Report(3, tenantId, first, T0.AddMinutes(59), nodeName: "node-1", fingerprint: FingerprintB), T0.AddMinutes(59), CancellationToken.None);

        await Assert.That((await ClusterAsync(dbFactory.Context, clusterId)).CaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, first)).FingerprintMismatch).IsTrue();
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, second)).FingerprintMismatch).IsFalse();
    }

    [Test]
    public async Task ApplyLocalReport_ReKeyIgnoresNodesThatHaveNotReportedWithinTheHour()
    {
        // Intent: a node that has been silent for more than an hour no longer vetoes a rotation. When
        // the only recent report carries the new fingerprint the cluster is re-keyed, and the silent
        // node, which last reported the old fingerprint, is the one left flagged.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long reporting = await SeedMachineAsync(dbFactory.Context, tenantId);
        long silent = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "node-1", reporting, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "node-2", silent, FingerprintA, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(5, tenantId, reporting, T0.AddMinutes(61), nodeName: "node-1", fingerprint: FingerprintB),
            T0.AddMinutes(61), CancellationToken.None);

        await Assert.That((await ClusterAsync(dbFactory.Context, clusterId)).CaFingerprint).IsEqualTo(FingerprintB);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, reporting)).FingerprintMismatch).IsFalse();
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, silent)).FingerprintMismatch).IsTrue();
    }

    [Test]
    public async Task ApplyLocalReport_NewNodeDuringRotation_JoinsExistingCluster()
    {
        // Intent: a machine that reports a fingerprint no cluster holds yet, while exactly one cluster
        // has a majority of its recently reporting nodes on that fingerprint, is a node joining
        // mid-rotation. It joins that cluster instead of founding a second one.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long rotatedOne = await SeedMachineAsync(dbFactory.Context, tenantId);
        long rotatedTwo = await SeedMachineAsync(dbFactory.Context, tenantId);
        long unrotated = await SeedMachineAsync(dbFactory.Context, tenantId);
        long newcomer = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "node-1", rotatedOne, FingerprintB, T0, mismatch: true);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "node-2", rotatedTwo, FingerprintB, T0, mismatch: true);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "node-3", unrotated, FingerprintA, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(9, tenantId, newcomer, T0.AddMinutes(5), nodeName: "node-4", fingerprint: FingerprintB),
            T0.AddMinutes(5), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
        KubernetesNode joined = await NodeOfMachineAsync(dbFactory.Context, newcomer);
        await Assert.That(joined.ClusterId).IsEqualTo(clusterId);
        await Assert.That(joined.FingerprintMismatch).IsTrue();
        await Assert.That((await ClusterAsync(dbFactory.Context, clusterId)).CaFingerprint).IsEqualTo(FingerprintA);
    }

    [Test]
    [Arguments(3600, false)]
    [Arguments(3601, true)]
    public async Task ApplyLocalReport_JoinByReportedFingerprint_HonoursTheOneHourWindow(int secondsSinceNodeReport, bool createsNewCluster)
    {
        // Intent: a node's report only counts as evidence of a rotation in progress for an hour. A node
        // that reported exactly an hour before still pulls the newcomer into its cluster, and one that
        // reported a second earlier than that no longer does, so the newcomer founds its own cluster.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long rotated = await SeedMachineAsync(dbFactory.Context, tenantId);
        long newcomer = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "node-1", rotated, FingerprintB, T0, mismatch: true);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(9, tenantId, newcomer, T0.AddSeconds(secondsSinceNodeReport), nodeName: "node-2", fingerprint: FingerprintB),
            T0.AddSeconds(secondsSinceNodeReport), CancellationToken.None);

        long newcomerCluster = (await NodeOfMachineAsync(dbFactory.Context, newcomer)).ClusterId;
        await Assert.That(outcome).IsEqualTo(createsNewCluster ? LocalReportOutcome.ClusterCreated : LocalReportOutcome.Grouped);
        await Assert.That(newcomerCluster == clusterId).IsEqualTo(createsNewCluster == false);
    }

    [Test]
    public async Task ApplyLocalReport_TwoClustersHaveRecentNodesWithTheFingerprint_CreatesNewCluster()
    {
        // Intent: join-by-reported-fingerprint only applies when exactly one cluster qualifies. With two
        // candidates the evidence is ambiguous, so the machine founds its own cluster instead of being
        // attached to a guess.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long m1 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long m2 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long newcomer = await SeedMachineAsync(dbFactory.Context, tenantId);
        long c1 = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long c2 = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintC, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, c1, "node-1", m1, FingerprintB, T0, mismatch: true);
        await SeedNodeAsync(dbFactory.Context, tenantId, c2, "node-2", m2, FingerprintB, T0, mismatch: true);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(9, tenantId, newcomer, T0.AddMinutes(5), nodeName: "node-3", fingerprint: FingerprintB),
            T0.AddMinutes(5), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.ClusterCreated);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(3);
        long created = (await NodeOfMachineAsync(dbFactory.Context, newcomer)).ClusterId;
        await Assert.That((await ClusterAsync(dbFactory.Context, created)).CaFingerprint).IsEqualTo(FingerprintB);
    }

    [Test]
    public async Task ApplyLocalReport_ReKeyBlockedWhenOtherClustersHoldNewFingerprint()
    {
        // Intent: a cluster never takes a fingerprint other clusters in the tenant already hold, because
        // clusters claiming one CA could no longer be told apart. With two other holders the node cannot
        // be moved to either, so it stays flagged in its own cluster, which keeps its key.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long otherMachine = await SeedMachineAsync(dbFactory.Context, tenantId);
        long thirdMachine = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long holder = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, T0);
        long secondHolder = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "node-1", machineId, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, holder, "other-1", otherMachine, FingerprintB, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, secondHolder, "third-1", thirdMachine, FingerprintB, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(6, tenantId, machineId, T0.AddMinutes(5), nodeName: "node-1", fingerprint: FingerprintB),
            T0.AddMinutes(5), CancellationToken.None);

        await Assert.That((await ClusterAsync(dbFactory.Context, clusterId)).CaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That((await ClusterAsync(dbFactory.Context, holder)).CaFingerprint).IsEqualTo(FingerprintB);
        KubernetesNode node = await NodeOfMachineAsync(dbFactory.Context, machineId);
        await Assert.That(node.ClusterId).IsEqualTo(clusterId);
        await Assert.That(node.FingerprintMismatch).IsTrue();
    }

    [Test]
    public async Task ApplyLocalReport_SingleNodeClusterReinstalled_ReKeysToTheNewFingerprint()
    {
        // Intent: a one-node cluster that is reinstalled with a new CA keeps its identity and its
        // display state instead of being abandoned for a new cluster, because its only recent node
        // agrees on the new fingerprint.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, machineId, T0), T0, CancellationToken.None);
        long clusterId = (await dbFactory.Context.KubernetesClusters.SingleAsync()).Id;

        await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0.AddHours(2), fingerprint: FingerprintB), T0.AddHours(2), CancellationToken.None);

        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
        await Assert.That((await ClusterAsync(dbFactory.Context, clusterId)).CaFingerprint).IsEqualTo(FingerprintB);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineId)).FingerprintMismatch).IsFalse();
    }

    [Test]
    public async Task ApplyLocalReport_NodeMovedToAnExistingCluster_LeavesItsOldClusterBehind()
    {
        // Intent: a machine whose node belongs to one cluster but now reports the CA of exactly one other
        // cluster in the tenant has moved clusters. Its node leaves the old cluster for the other one,
        // and the cluster it left keeps its key and its remaining nodes, none of them flagged.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long m1 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long m2 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long mover = await SeedMachineAsync(dbFactory.Context, tenantId);
        long mb = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterA = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long clusterB = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterA, "a-1", m1, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterA, "a-2", m2, FingerprintA, T0);
        long oldRow = await SeedNodeAsync(dbFactory.Context, tenantId, clusterA, "a-3", mover, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterB, "b-1", mb, FingerprintB, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(10, tenantId, mover, T0.AddMinutes(10), nodeName: "a-3", fingerprint: FingerprintB),
            T0.AddMinutes(10), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        KubernetesNode moved = await NodeOfMachineAsync(dbFactory.Context, mover);
        await Assert.That(moved.ClusterId).IsEqualTo(clusterB);
        await Assert.That(moved.Id == oldRow).IsFalse();
        await Assert.That(moved.FingerprintMismatch).IsFalse();
        await Assert.That(moved.ReportedCaFingerprint).IsEqualTo(FingerprintB);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.Id == oldRow)).IsEqualTo(0);

        List<KubernetesNode> remaining = await dbFactory.Context.KubernetesNodes.Where(n => n.ClusterId == clusterA).ToListAsync();
        await Assert.That(remaining.Select(n => n.NodeName).OrderBy(n => n).ToList()).IsEquivalentTo(new List<string> { "a-1", "a-2" });
        await Assert.That(remaining.Any(n => n.FingerprintMismatch)).IsFalse();
        await Assert.That((await ClusterAsync(dbFactory.Context, clusterA)).CaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That((await ClusterAsync(dbFactory.Context, clusterB)).CaFingerprint).IsEqualTo(FingerprintB);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.ClusterId == clusterB)).IsEqualTo(2);
    }

    [Test]
    public async Task ApplyLocalReport_NodeReportsCaHeldByTwoOtherClusters_StaysFlaggedInItsCluster()
    {
        // Intent: moving a node needs unambiguous evidence. When two other clusters hold the fingerprint
        // the node reports, the report does not say which one it joined, so the node stays in its own
        // cluster, flagged, instead of being moved on a guess.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long mover = await SeedMachineAsync(dbFactory.Context, tenantId);
        long sibling = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterA = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, T0);
        await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterA, "a-1", mover, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterA, "a-2", sibling, FingerprintA, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(10, tenantId, mover, T0.AddMinutes(10), nodeName: "a-1", fingerprint: FingerprintB),
            T0.AddMinutes(10), CancellationToken.None);

        KubernetesNode node = await NodeOfMachineAsync(dbFactory.Context, mover);
        await Assert.That(node.ClusterId).IsEqualTo(clusterA);
        await Assert.That(node.FingerprintMismatch).IsTrue();
    }

    [Test]
    public async Task ApplyLocalReport_NodeMovedToABrandNewCluster_FollowsTheNewHostOnItsNextReport()
    {
        // Intent: one node of a cluster reporting a new CA alone is not a rotation, so a new host
        // reporting that CA founds its own cluster instead of being pulled into the old one. The node
        // that reported first then follows on its next report, and the old cluster keeps its key and its
        // other nodes, none of them flagged.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long m1 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long m2 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long m3 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long m5 = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, m1, T0, nodeName: "n1"), T0, CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(Report(2, tenantId, m2, T0, nodeName: "n2"), T0, CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(Report(3, tenantId, m3, T0, nodeName: "n3"), T0, CancellationToken.None);
        long clusterA = (await dbFactory.Context.KubernetesClusters.SingleAsync()).Id;

        await repo.ApplyLocalNodeReportAsync(
            Report(4, tenantId, m3, T0.AddMinutes(10), nodeName: "n3", fingerprint: FingerprintB), T0.AddMinutes(10), CancellationToken.None);
        LocalReportOutcome newHost = await repo.ApplyLocalNodeReportAsync(
            Report(5, tenantId, m5, T0.AddMinutes(11), nodeName: "n5", fingerprint: FingerprintB), T0.AddMinutes(11), CancellationToken.None);

        await Assert.That(newHost).IsEqualTo(LocalReportOutcome.ClusterCreated);
        long clusterB = (await NodeOfMachineAsync(dbFactory.Context, m5)).ClusterId;
        await Assert.That(clusterB == clusterA).IsFalse();
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, m3)).ClusterId).IsEqualTo(clusterA);

        await repo.ApplyLocalNodeReportAsync(
            Report(6, tenantId, m3, T0.AddMinutes(25), nodeName: "n3", fingerprint: FingerprintB), T0.AddMinutes(25), CancellationToken.None);

        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, m3)).ClusterId).IsEqualTo(clusterB);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.ClusterId == clusterB)).IsEqualTo(2);
        List<KubernetesNode> remaining = await dbFactory.Context.KubernetesNodes.Where(n => n.ClusterId == clusterA).ToListAsync();
        await Assert.That(remaining.Select(n => n.NodeName).OrderBy(n => n).ToList()).IsEquivalentTo(new List<string> { "n1", "n2" });
        await Assert.That(remaining.Any(n => n.FingerprintMismatch)).IsFalse();
        await Assert.That((await ClusterAsync(dbFactory.Context, clusterA)).CaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That((await ClusterAsync(dbFactory.Context, clusterB)).CaFingerprint).IsEqualTo(FingerprintB);
    }

    [Test]
    public async Task ApplyLocalReport_RotationWithMajorityRotated_NewNodeJoinsTheRotatingCluster()
    {
        // Intent: when two of a cluster's three recently reporting nodes already report the new CA, the
        // rotation is well under way, so a new node reporting that CA joins the rotating cluster instead
        // of founding a second one.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long m1 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long m2 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long m3 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long m4 = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, m1, T0, nodeName: "n1"), T0, CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(Report(2, tenantId, m2, T0, nodeName: "n2"), T0, CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(Report(3, tenantId, m3, T0, nodeName: "n3"), T0, CancellationToken.None);
        long clusterA = (await dbFactory.Context.KubernetesClusters.SingleAsync()).Id;
        await repo.ApplyLocalNodeReportAsync(
            Report(4, tenantId, m1, T0.AddMinutes(10), nodeName: "n1", fingerprint: FingerprintB), T0.AddMinutes(10), CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(
            Report(5, tenantId, m2, T0.AddMinutes(20), nodeName: "n2", fingerprint: FingerprintB), T0.AddMinutes(20), CancellationToken.None);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(6, tenantId, m4, T0.AddMinutes(30), nodeName: "n4", fingerprint: FingerprintB), T0.AddMinutes(30), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
        KubernetesNode joined = await NodeOfMachineAsync(dbFactory.Context, m4);
        await Assert.That(joined.ClusterId).IsEqualTo(clusterA);
        await Assert.That((await ClusterAsync(dbFactory.Context, clusterA)).CaFingerprint).IsEqualTo(FingerprintA);
    }

    [Test]
    public async Task ApplyLocalReport_RotationWithMinorityRotated_NewNodeFoundsAClusterAndTheOthersFollow()
    {
        // Intent: one drifted node out of three is not a rotation, so a new node reporting its CA founds
        // a cluster of its own. As the old cluster's nodes rotate they each move to it, the old cluster
        // is left with no nodes, and garbage collection deletes it.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long m1 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long m2 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long m3 = await SeedMachineAsync(dbFactory.Context, tenantId);
        long m4 = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, m1, T0, nodeName: "n1"), T0, CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(Report(2, tenantId, m2, T0, nodeName: "n2"), T0, CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(Report(3, tenantId, m3, T0, nodeName: "n3"), T0, CancellationToken.None);
        long clusterA = (await dbFactory.Context.KubernetesClusters.SingleAsync()).Id;
        await repo.ApplyLocalNodeReportAsync(
            Report(4, tenantId, m1, T0.AddMinutes(10), nodeName: "n1", fingerprint: FingerprintB), T0.AddMinutes(10), CancellationToken.None);

        LocalReportOutcome newNode = await repo.ApplyLocalNodeReportAsync(
            Report(5, tenantId, m4, T0.AddMinutes(20), nodeName: "n4", fingerprint: FingerprintB), T0.AddMinutes(20), CancellationToken.None);

        await Assert.That(newNode).IsEqualTo(LocalReportOutcome.ClusterCreated);
        long clusterB = (await NodeOfMachineAsync(dbFactory.Context, m4)).ClusterId;
        await Assert.That(clusterB == clusterA).IsFalse();

        await repo.ApplyLocalNodeReportAsync(
            Report(6, tenantId, m1, T0.AddMinutes(30), nodeName: "n1", fingerprint: FingerprintB), T0.AddMinutes(30), CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(
            Report(7, tenantId, m2, T0.AddMinutes(40), nodeName: "n2", fingerprint: FingerprintB), T0.AddMinutes(40), CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(
            Report(8, tenantId, m3, T0.AddMinutes(50), nodeName: "n3", fingerprint: FingerprintB), T0.AddMinutes(50), CancellationToken.None);

        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.ClusterId == clusterB)).IsEqualTo(4);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.ClusterId == clusterA)).IsEqualTo(0);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.FingerprintMismatch)).IsEqualTo(0);

        int deleted = await repo.CollectGarbageAsync(T0.AddMinutes(60), CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(1);
        KubernetesCluster survivor = await dbFactory.Context.KubernetesClusters.SingleAsync();
        await Assert.That(survivor.Id).IsEqualTo(clusterB);
        await Assert.That(survivor.CaFingerprint).IsEqualTo(FingerprintB);
    }

    [Test]
    public async Task ApplyLocalReport_RotationJoinCountsOnlyNodesThatReportedWithinTheHour()
    {
        // Intent: the majority is taken over the nodes that reported in the last hour. Two of three nodes
        // on the new CA are a majority, but when one of them has been silent for more than an hour only
        // one of two recent nodes agrees, which is a tie and not a majority, so the newcomer founds a
        // cluster.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long rotatedRecent = await SeedMachineAsync(dbFactory.Context, tenantId);
        long rotatedSilent = await SeedMachineAsync(dbFactory.Context, tenantId);
        long unrotated = await SeedMachineAsync(dbFactory.Context, tenantId);
        long newcomer = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "n1", rotatedRecent, FingerprintB, T0.AddMinutes(100), mismatch: true);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "n2", rotatedSilent, FingerprintB, T0.AddMinutes(20), mismatch: true);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "n3", unrotated, FingerprintA, T0.AddMinutes(100));

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(9, tenantId, newcomer, T0.AddMinutes(110), nodeName: "n4", fingerprint: FingerprintB),
            T0.AddMinutes(110), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.ClusterCreated);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, newcomer)).ClusterId == clusterId).IsFalse();
    }

    [Test]
    [Arguments("unknown", "kubeadm", "kubeadm")]
    [Arguments("k3s", "rke2", "rke2")]
    [Arguments("k3s", "unknown", "k3s")]
    [Arguments("kubeadm", "kubeadm", "kubeadm")]
    public async Task ApplyLocalReport_ClusterDistroFollowsTheLatestKnownDistro(string stored, string reported, string expected)
    {
        // Intent: a cluster first seen by a host that could not name its distro learns it from a later
        // report, and a later known distro replaces an earlier one, but a report that cannot name the
        // distro never erases what the cluster already holds.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, machineId, T0, distro: stored), T0, CancellationToken.None);

        await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0.AddMinutes(15), distro: reported), T0.AddMinutes(15), CancellationToken.None);

        await Assert.That((await dbFactory.Context.KubernetesClusters.SingleAsync()).Distro).IsEqualTo(expected);
    }

    [Test]
    public async Task ApplyLocalReport_SecondMachineWithAKnownDistro_RefreshesAClusterFoundedAsUnknown()
    {
        // Intent: the distro is refreshed for a machine joining the cluster as well as for an established
        // node, so a cluster founded by a host with an unrecognised layout is named correctly as soon as
        // any member reports a known distro.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long first = await SeedMachineAsync(dbFactory.Context, tenantId);
        long second = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, first, T0, nodeName: "n1", distro: "unknown"), T0, CancellationToken.None);

        await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, second, T0.AddMinutes(1), nodeName: "n2", distro: "rke2"), T0.AddMinutes(1), CancellationToken.None);

        await Assert.That((await dbFactory.Context.KubernetesClusters.SingleAsync()).Distro).IsEqualTo("rke2");
    }

    [Test]
    public async Task ApplyLocalReport_CursorNeverMovesBackwards()
    {
        // Intent: a replica that lost the projection lock mid-batch can still finish a row with a lower
        // telemetry id. It must never rewind the cursor its successor has already advanced.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long otherMachine = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(100, tenantId, machineId, T0), T0, CancellationToken.None);

        await repo.ApplyLocalNodeReportAsync(
            Report(50, tenantId, otherMachine, T0, hasKubelet: false), T0.AddMinutes(1), CancellationToken.None);

        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(100L);
    }

    [Test]
    public async Task SetProjectionCursor_StartsEmptyAdvancesAndNeverRewinds()
    {
        // Intent: the standalone cursor write used for skipped rows creates the single cursor row on
        // first use, moves it forward, and ignores a lower position, leaving its timestamp alone
        // because the cursor did not advance.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsNull();

        await repo.SetProjectionCursorAsync(10, T0, CancellationToken.None);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(10L);

        await repo.SetProjectionCursorAsync(200, T0.AddMinutes(1), CancellationToken.None);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(200L);

        await repo.SetProjectionCursorAsync(150, T0.AddMinutes(2), CancellationToken.None);

        KubernetesInventoryProjectionCursor row = await dbFactory.Context.KubernetesInventoryProjectionCursors.SingleAsync();
        await Assert.That(row.Id).IsEqualTo(1);
        await Assert.That(row.Position).IsEqualTo(200L);
        await Assert.That(row.UpdatedAt).IsEqualTo(T0.AddMinutes(1));
    }

    [Test]
    public async Task ApplyLocalReport_NodeLinkedToOtherMachine_StealsAndUnlinksPrevious()
    {
        // Intent: when a different machine reports a node name another machine holds, the node follows
        // the newest reporter, the previous machine is left with no node, and the move is logged at
        // warning. The unique machine link must stay satisfied throughout.
        using TestDatabaseFactory dbFactory = new();
        CapturingLogger logger = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory, logger);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long previous = await SeedMachineAsync(dbFactory.Context, tenantId);
        long taker = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, previous, T0, nodeName: "node-1"), T0, CancellationToken.None);
        KubernetesNode original = await dbFactory.Context.KubernetesNodes.SingleAsync();

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, taker, T0.AddMinutes(1), nodeName: "node-1"), T0.AddMinutes(1), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await Assert.That(node.Id).IsEqualTo(original.Id);
        await Assert.That(node.MachineId).IsEqualTo(taker);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.MachineId == previous)).IsEqualTo(0);
        (LogLevel Level, string Message) warning = logger.Entries.Single(e => e.Level == LogLevel.Warning);
        await Assert.That(warning.Message).IsEqualTo(
            $"Kubernetes node node-1 in cluster {node.ClusterId} moved from machine {previous} to {taker}");
    }

    [Test]
    public async Task ApplyLocalReport_TakingANodeWhileAlreadyLinkedElsewhere_ReleasesTheOldLinkFirst()
    {
        // Intent: a machine that already is one node and reports the name of a node another machine
        // holds gives up its own row and takes the other, ending with one node per machine and no
        // unique-link violation along the way.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long mover = await SeedMachineAsync(dbFactory.Context, tenantId);
        long holder = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long moverRow = await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "host-a", mover, FingerprintA, T0);
        long heldRow = await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "node-a", holder, FingerprintA, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(3, tenantId, mover, T0.AddMinutes(1), nodeName: "node-a"), T0.AddMinutes(1), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.Id == moverRow)).IsEqualTo(0);
        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await Assert.That(node.Id).IsEqualTo(heldRow);
        await Assert.That(node.MachineId).IsEqualTo(mover);
    }

    [Test]
    public async Task ApplyLocalReport_TwoTenantsSameFingerprint_GetSeparateClusters()
    {
        // Intent: tenants never share inventory. Two tenants whose machines report the same CA
        // fingerprint each get their own cluster, and neither sees the other's nodes.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantOne = await SeedTenantAsync(dbFactory.Context);
        int tenantTwo = await SeedTenantAsync(dbFactory.Context);
        long machineOne = await SeedMachineAsync(dbFactory.Context, tenantOne);
        long machineTwo = await SeedMachineAsync(dbFactory.Context, tenantTwo);

        LocalReportOutcome first = await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantOne, machineOne, T0, nodeName: "node-1"), T0, CancellationToken.None);
        LocalReportOutcome second = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantTwo, machineTwo, T0, nodeName: "node-1"), T0, CancellationToken.None);

        await Assert.That(first).IsEqualTo(LocalReportOutcome.ClusterCreated);
        await Assert.That(second).IsEqualTo(LocalReportOutcome.ClusterCreated);
        List<KubernetesCluster> clusters = await dbFactory.Context.KubernetesClusters.OrderBy(c => c.Id).ToListAsync();
        await Assert.That(clusters.Count).IsEqualTo(2);
        await Assert.That(clusters[0].TenantId).IsEqualTo(tenantOne);
        await Assert.That(clusters[1].TenantId).IsEqualTo(tenantTwo);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineOne)).ClusterId).IsEqualTo(clusters[0].Id);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineTwo)).ClusterId).IsEqualTo(clusters[1].Id);
    }

    [Test]
    public async Task CollectGarbage_DeletesStaleNodesAndEmptyClusters_KeepsFresh()
    {
        // Intent: a node nobody has reported for seven days, or that no local report ever described, is
        // removed, and so is any cluster left with no nodes. A node exactly seven days old is kept, and
        // other tenants' fresh clusters are untouched. The count is every row removed.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantOne = await SeedTenantAsync(db);
        int tenantTwo = await SeedTenantAsync(db);
        DateTimeOffset now = T0.AddDays(30);

        long mixed = await SeedClusterAsync(db, tenantOne, FingerprintA, now.AddDays(-20));
        long freshNode = await SeedNodeAsync(db, tenantOne, mixed, "fresh", null, FingerprintA, now.AddDays(-1));
        long boundaryNode = await SeedNodeAsync(db, tenantOne, mixed, "boundary", null, FingerprintA, now.AddDays(-7));
        long staleNode = await SeedNodeAsync(db, tenantOne, mixed, "stale", null, FingerprintA, now.AddDays(-7).AddTicks(-1));
        long neverReported = await SeedNodeAsync(db, tenantOne, mixed, "never", null, null, null);
        long staleOnly = await SeedClusterAsync(db, tenantOne, FingerprintB, now.AddDays(-20));
        long staleOnlyNode = await SeedNodeAsync(db, tenantOne, staleOnly, "old", null, FingerprintB, now.AddDays(-9));
        long empty = await SeedClusterAsync(db, tenantOne, FingerprintC, now.AddDays(-20));
        long otherTenant = await SeedClusterAsync(db, tenantTwo, FingerprintA, now.AddDays(-1));
        long otherTenantNode = await SeedNodeAsync(db, tenantTwo, otherTenant, "fresh", null, FingerprintA, now.AddDays(-1));

        int deleted = await repo.CollectGarbageAsync(now, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(5);
        List<long> remainingNodes = await db.KubernetesNodes.Select(n => n.Id).OrderBy(id => id).ToListAsync();
        await Assert.That(remainingNodes).IsEquivalentTo(new List<long> { freshNode, boundaryNode, otherTenantNode });
        List<long> remainingClusters = await db.KubernetesClusters.Select(c => c.Id).OrderBy(id => id).ToListAsync();
        await Assert.That(remainingClusters).IsEquivalentTo(new List<long> { mixed, otherTenant });
        await Assert.That(new[] { staleNode, neverReported, staleOnlyNode }.Intersect(remainingNodes).Count()).IsEqualTo(0);
        await Assert.That(remainingClusters.Contains(empty)).IsFalse();
        await Assert.That(remainingClusters.Contains(staleOnly)).IsFalse();
    }

    [Test]
    public async Task CollectGarbage_NothingStored_DeletesNothing()
    {
        // Intent: garbage collection on an empty inventory is a harmless no-op that reports zero.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);

        int deleted = await repo.CollectGarbageAsync(T0, CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(0);
    }

    [Test]
    public async Task GetKubernetesTelemetryBatch_FiltersTypeWindowAndVisibility()
    {
        // Intent: the projector reads only Kubernetes telemetry types, only rows received inside its
        // window, and only rows that have been visible for the safety lag, in id order and capped at the
        // batch size, resuming after the cursor.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        DateTimeOffset receivedAfter = T0.AddDays(-8);
        DateTimeOffset visibleBefore = T0.AddSeconds(-30);

        async Task<long> InsertRowAsync(short type, DateTimeOffset receivedAt, DateTimeOffset serverReceivedAt)
        {
            return await db.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachineTelemetry(
                telemetryType: type, receivedAt: receivedAt, serverReceivedAt: serverReceivedAt));
        }

        long kubernetesNode = await InsertRowAsync(14, T0.AddMinutes(-5), T0.AddMinutes(-5));
        await InsertRowAsync(6, T0.AddMinutes(-5), T0.AddMinutes(-5));
        long kubernetesReserved = await InsertRowAsync(15, T0.AddMinutes(-5), T0.AddMinutes(-5));
        await InsertRowAsync(14, receivedAfter, T0.AddMinutes(-5));
        await InsertRowAsync(14, T0.AddMinutes(-5), visibleBefore.AddTicks(1));
        long atVisibilityEdge = await InsertRowAsync(14, T0.AddMinutes(-5), visibleBefore);
        long afterEdge = await InsertRowAsync(14, receivedAfter.AddTicks(1), T0.AddMinutes(-4));
        long last = await InsertRowAsync(14, T0.AddMinutes(-3), T0.AddMinutes(-3));

        IReadOnlyList<MachineTelemetry> firstPage = await repo.GetKubernetesTelemetryBatchAsync(
            0, receivedAfter, visibleBefore, 3, CancellationToken.None);
        IReadOnlyList<MachineTelemetry> secondPage = await repo.GetKubernetesTelemetryBatchAsync(
            atVisibilityEdge, receivedAfter, visibleBefore, 10, CancellationToken.None);

        await Assert.That(firstPage.Select(r => r.Id).SequenceEqual(new[] { kubernetesNode, kubernetesReserved, atVisibilityEdge })).IsTrue();
        await Assert.That(secondPage.Select(r => r.Id).SequenceEqual(new[] { afterEdge, last })).IsTrue();
        await Assert.That(firstPage.Concat(secondPage).All(r => (r.TelemetryType == 14) || (r.TelemetryType == 15))).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task ApplyLocalReport_NoKubeletVersion_KeepsTheStoredVersion(string? kubeletVersion)
    {
        // Intent: a report that could not read the kubelet version, whether it sent nothing or an empty
        // value, must not erase the version an earlier report established; the rest of the node is
        // still refreshed.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0, kubeletVersion: "v1.30.4+k3s1"), T0, CancellationToken.None);

        await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0.AddMinutes(15), controlPlane: true, kubeletVersion: kubeletVersion),
            T0.AddMinutes(15), CancellationToken.None);

        KubernetesNode node = await NodeOfMachineAsync(dbFactory.Context, machineId);
        await Assert.That(node.KubeletVersion).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(node.LocalControlPlane).IsTrue();
        await Assert.That(node.LastLocalReportAt).IsEqualTo(T0.AddMinutes(15));
    }

    [Test]
    public async Task ApplyLocalReport_FirstReportWithEmptyKubeletVersion_StoresNone()
    {
        // Intent: with nothing stored and nothing reported, the node has no version, represented as
        // null rather than an empty string.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0, kubeletVersion: ""), T0, CancellationToken.None);

        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineId)).KubeletVersion).IsNull();
    }

    [Test]
    public async Task ApplyLocalReport_RenamedWithoutFingerprintOrVersion_CarriesTheOldRowsValuesForward()
    {
        // Intent: when a node is renamed by a report that carries no fingerprint or version, the row
        // that replaces the old one keeps what the old row had established, so the rename does not
        // forget the fingerprint the machine last reported or its kubelet version.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0, nodeName: "host-a", kubeletVersion: "v1.30.4+k3s1"), T0, CancellationToken.None);

        await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0.AddMinutes(15), nodeName: "node-a", fingerprint: null, kubeletVersion: null),
            T0.AddMinutes(15), CancellationToken.None);

        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await Assert.That(node.NodeName).IsEqualTo("node-a");
        await Assert.That(node.ReportedCaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That(node.KubeletVersion).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(node.FingerprintMismatch).IsFalse();
    }

    [Test]
    public async Task ApplyLocalReport_ReKeyNotBlockedByAnotherTenantHoldingTheNewFingerprint()
    {
        // Intent: the rule that a cluster never takes a fingerprint another cluster holds is scoped to
        // the tenant. Another tenant's cluster with the same CA is unrelated inventory and must neither
        // block this tenant's re-key nor change.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantOne = await SeedTenantAsync(dbFactory.Context);
        int tenantTwo = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantOne);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantOne, machineId, T0), T0, CancellationToken.None);
        long clusterId = (await dbFactory.Context.KubernetesClusters.SingleAsync()).Id;
        long otherTenantCluster = await SeedClusterAsync(dbFactory.Context, tenantTwo, FingerprintB, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantOne, machineId, T0.AddHours(2), fingerprint: FingerprintB), T0.AddHours(2), CancellationToken.None);

        await Assert.That((await ClusterAsync(dbFactory.Context, clusterId)).CaFingerprint).IsEqualTo(FingerprintB);
        await Assert.That((await ClusterAsync(dbFactory.Context, otherTenantCluster)).CaFingerprint).IsEqualTo(FingerprintB);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineId)).FingerprintMismatch).IsFalse();
    }

    [Test]
    public async Task ApplyLocalReport_CursorInsertFails_RollsBackTheClusterAndNode()
    {
        // Intent: the cluster and node writes and the cursor advance are one unit of work. When the
        // cursor write fails after the node has been written, nothing survives, so a retry starts from
        // the same state and no row is ever applied without its cursor moving. The connection must also
        // be left usable.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        await dbFactory.Context.ExecuteAsync(
            """
            CREATE TRIGGER RefuseCursorInsert BEFORE INSERT ON "KubernetesInventoryProjectionCursor"
            BEGIN SELECT RAISE(ABORT, 'cursor write refused'); END
            """);

        SqliteException? failure = await Assert.ThrowsAsync<SqliteException>(
            async () => await repo.ApplyLocalNodeReportAsync(Report(7, tenantId, machineId, T0), T0, CancellationToken.None));

        await Assert.That(failure!.Message).Contains("cursor write refused");
        await AssertNothingWrittenAsync(dbFactory.Context);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsNull();

        await dbFactory.Context.ExecuteAsync("DROP TRIGGER RefuseCursorInsert");
        LocalReportOutcome retried = await repo.ApplyLocalNodeReportAsync(Report(7, tenantId, machineId, T0), T0, CancellationToken.None);

        await Assert.That(retried).IsEqualTo(LocalReportOutcome.ClusterCreated);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(7L);
    }

    [Test]
    public async Task ApplyLocalReport_CursorUpdateFails_RollsBackNodeCursorAndClusterChanges()
    {
        // Intent: the same unit of work holds for a report that updates existing rows. When the cursor
        // update fails, the node's new values, its new name and the cluster's last-seen time are rolled
        // back with it, and the cursor keeps its earlier position.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(
            Report(10, tenantId, machineId, T0, nodeName: "host-a", kubeletVersion: "v1.30.4+k3s1"), T0, CancellationToken.None);
        await dbFactory.Context.ExecuteAsync(
            """
            CREATE TRIGGER RefuseCursorUpdate BEFORE UPDATE ON "KubernetesInventoryProjectionCursor"
            BEGIN SELECT RAISE(ABORT, 'cursor write refused'); END
            """);

        SqliteException? failure = await Assert.ThrowsAsync<SqliteException>(
            async () => await repo.ApplyLocalNodeReportAsync(
                Report(20, tenantId, machineId, T0.AddMinutes(15), nodeName: "node-a", kubeletVersion: "v1.31.0+k3s1"),
                T0.AddMinutes(15), CancellationToken.None));

        await Assert.That(failure!.Message).Contains("cursor write refused");
        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await Assert.That(node.NodeName).IsEqualTo("host-a");
        await Assert.That(node.KubeletVersion).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(node.LastLocalReportAt).IsEqualTo(T0);
        await Assert.That((await dbFactory.Context.KubernetesClusters.SingleAsync()).LastSeenAt).IsEqualTo(T0);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(10L);
    }

    private sealed class CapturingLogger : ILogger<Database.Repositories.DatabaseRepository>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
