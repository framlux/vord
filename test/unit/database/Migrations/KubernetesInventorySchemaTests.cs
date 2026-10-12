// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;

namespace Framlux.FleetManagement.Test.Migrations;

/// <summary>
/// Schema and model round-trip tests for the Kubernetes inventory tables created by
/// <see cref="Database.Migrations.AddKubernetesInventory"/>: the clusters a tenant's machines
/// belong to, the nodes inside them, and the cursor the inventory projection resumes from. It also
/// covers the cluster reporting columns and the reporter candidates table that
/// <see cref="Database.Migrations.AddKubernetesReporting"/> adds.
/// </summary>
public class KubernetesInventorySchemaTests
{
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string KubeSystemUid = "6f1c2e0a-3b7d-4c55-9a1e-0d2f8b7c4e19";
    private const string LeaseUid = "a4d3c2b1-0f9e-4d8c-b7a6-5e4f3a2b1c0d";
    private const string RolesJson = """["control-plane","etcd"]""";
    private const string ConditionsJson = """[{"type":"Ready","status":"True","lastTransitionTime":"2026-10-11T08:00:00Z"},{"type":"MemoryPressure","status":"False","lastTransitionTime":"2026-10-11T08:00:00Z"}]""";

    private static readonly DateTimeOffset FirstSeenAt = new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ClusterAndNode_InsertAndRead_RoundTripsAllFields()
    {
        using TestDatabaseFactory dbFactory = new();

        DateTimeOffset lastSeenAt = FirstSeenAt.AddHours(2);
        DateTimeOffset lastLocalReportAt = FirstSeenAt.AddHours(1);

        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = 7,
            CaFingerprint = Fingerprint,
            DisplayName = "Production",
            Distro = "k3s",
            FirstSeenAt = FirstSeenAt,
            LastSeenAt = lastSeenAt,
        });

        long nodeId = await dbFactory.Context.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = 7,
            ClusterId = clusterId,
            NodeName = "worker-1.example.com",
            MachineId = 42,
            ReportedCaFingerprint = Fingerprint,
            FingerprintMismatch = true,
            LocalControlPlane = true,
            KubeletVersion = "v1.31.2+k3s1",
            LastLocalReportAt = lastLocalReportAt,
        });

        KubernetesCluster? cluster = await dbFactory.Context.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId);
        KubernetesNode? node = await dbFactory.Context.KubernetesNodes
            .FirstOrDefaultAsync(n => n.Id == nodeId);

        await Assert.That(cluster).IsNotNull();
        await Assert.That(cluster!.TenantId).IsEqualTo(7);
        await Assert.That(cluster.CaFingerprint).IsEqualTo(Fingerprint);
        await Assert.That(cluster.DisplayName).IsEqualTo("Production");
        await Assert.That(cluster.Distro).IsEqualTo("k3s");
        await Assert.That(cluster.FirstSeenAt).IsEqualTo(FirstSeenAt);
        await Assert.That(cluster.LastSeenAt).IsEqualTo(lastSeenAt);

        await Assert.That(node).IsNotNull();
        await Assert.That(node!.TenantId).IsEqualTo(7);
        await Assert.That(node.ClusterId).IsEqualTo(clusterId);
        await Assert.That(node.NodeName).IsEqualTo("worker-1.example.com");
        await Assert.That(node.MachineId).IsEqualTo(42L);
        await Assert.That(node.ReportedCaFingerprint).IsEqualTo(Fingerprint);
        await Assert.That(node.FingerprintMismatch).IsTrue();
        await Assert.That(node.LocalControlPlane).IsTrue();
        await Assert.That(node.KubeletVersion).IsEqualTo("v1.31.2+k3s1");
        await Assert.That(node.LastLocalReportAt).IsEqualTo(lastLocalReportAt);
    }

    [Test]
    public async Task ClusterAndNode_NullableColumnsLeftNull_ReadBackAsNull()
    {
        using TestDatabaseFactory dbFactory = new();

        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));
        long nodeId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildNode(clusterId, "node-a", machineId: null));

        KubernetesCluster? cluster = await dbFactory.Context.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId);
        KubernetesNode? node = await dbFactory.Context.KubernetesNodes
            .FirstOrDefaultAsync(n => n.Id == nodeId);

        await Assert.That(cluster!.DisplayName).IsNull();
        await Assert.That(node!.MachineId).IsNull();
        await Assert.That(node.ReportedCaFingerprint).IsNull();
        await Assert.That(node.KubeletVersion).IsNull();
        await Assert.That(node.LastLocalReportAt).IsNull();
    }

    [Test]
    public async Task Nodes_BooleanColumnsOmittedFromInsert_DefaultToFalse()
    {
        using TestDatabaseFactory dbFactory = new();

        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));

        await dbFactory.Context.ExecuteAsync(
            """INSERT INTO "KubernetesNodes" ("TenantId", "ClusterId", "NodeName") VALUES (1, @clusterId, 'node-a')""",
            new DataParameter("clusterId", clusterId));

        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();

        await Assert.That(node.FingerprintMismatch).IsFalse();
        await Assert.That(node.LocalControlPlane).IsFalse();
    }

    [Test]
    public async Task Nodes_SameClusterAndNodeName_ThrowsUniqueConstraintViolation()
    {
        // Two machines that present the same node name inside one cluster are the same node, so the
        // second insert must collide rather than create a duplicate row.
        using TestDatabaseFactory dbFactory = new();

        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));
        await dbFactory.Context.InsertWithInt64IdentityAsync(BuildNode(clusterId, "node-a", machineId: 1));

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await dbFactory.Context.InsertWithInt64IdentityAsync(BuildNode(clusterId, "node-a", machineId: 2)));
    }

    [Test]
    public async Task Nodes_SameNodeNameInDifferentClusters_IsAllowed()
    {
        using TestDatabaseFactory dbFactory = new();

        long firstClusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));
        long secondClusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: new string('a', 64)));

        await dbFactory.Context.InsertWithInt64IdentityAsync(BuildNode(firstClusterId, "node-a", machineId: 1));
        long secondNodeId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildNode(secondClusterId, "node-a", machineId: 2));

        await Assert.That(secondNodeId).IsNotEqualTo(0L);
    }

    [Test]
    public async Task Nodes_SameNonNullMachineId_ThrowsUniqueConstraintViolation()
    {
        // A machine is at most one node. The partial unique index is what lets the projection move a
        // machine's node row atomically instead of leaving the machine linked twice.
        using TestDatabaseFactory dbFactory = new();

        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));
        await dbFactory.Context.InsertWithInt64IdentityAsync(BuildNode(clusterId, "node-a", machineId: 5));

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await dbFactory.Context.InsertWithInt64IdentityAsync(BuildNode(clusterId, "node-b", machineId: 5)));
    }

    [Test]
    public async Task Nodes_SameNonNullMachineIdInDifferentClusters_ThrowsUniqueConstraintViolation()
    {
        using TestDatabaseFactory dbFactory = new();

        long firstClusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));
        long secondClusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: new string('a', 64)));
        await dbFactory.Context.InsertWithInt64IdentityAsync(BuildNode(firstClusterId, "node-a", machineId: 5));

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await dbFactory.Context.InsertWithInt64IdentityAsync(BuildNode(secondClusterId, "node-b", machineId: 5)));
    }

    [Test]
    public async Task Nodes_MultipleNodesWithNullMachineId_AreAllowed()
    {
        // Nodes that no enrolled machine has reported yet carry no machine link, and any number of
        // them can exist at once.
        using TestDatabaseFactory dbFactory = new();

        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));
        await dbFactory.Context.InsertWithInt64IdentityAsync(BuildNode(clusterId, "node-a", machineId: null));
        await dbFactory.Context.InsertWithInt64IdentityAsync(BuildNode(clusterId, "node-b", machineId: null));

        int unlinked = await dbFactory.Context.KubernetesNodes.CountAsync(n => n.MachineId == null);

        await Assert.That(unlinked).IsEqualTo(2);
    }

    [Test]
    public async Task Clusters_SameTenantAndFingerprint_IsAllowed()
    {
        // The fingerprint index is a lookup, not a constraint: two clusters can legitimately share one
        // CA, and the projection must be able to store both rather than fail the second insert.
        using TestDatabaseFactory dbFactory = new();

        await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));
        long secondId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));

        int sharing = await dbFactory.Context.KubernetesClusters
            .CountAsync(c => (c.TenantId == 1) && (c.CaFingerprint == Fingerprint));

        await Assert.That(secondId).IsNotEqualTo(0L);
        await Assert.That(sharing).IsEqualTo(2);
    }

    [Test]
    public async Task InventoryProjectionCursor_InsertAndRead_RoundTripsAllFields()
    {
        using TestDatabaseFactory dbFactory = new();

        DateTimeOffset updatedAt = FirstSeenAt.AddMinutes(5);

        await dbFactory.Context.InsertAsync(new KubernetesInventoryProjectionCursor
        {
            Id = 1,
            Position = 9_876_543_210L,
            UpdatedAt = updatedAt,
        });

        KubernetesInventoryProjectionCursor? cursor = await dbFactory.Context.KubernetesInventoryProjectionCursors
            .FirstOrDefaultAsync(c => c.Id == 1);

        await Assert.That(cursor).IsNotNull();
        await Assert.That(cursor!.Position).IsEqualTo(9_876_543_210L);
        await Assert.That(cursor.UpdatedAt).IsEqualTo(updatedAt);
    }

    [Test]
    public async Task InventoryProjectionCursor_SecondRowWithSameId_ThrowsUniqueConstraintViolation()
    {
        using TestDatabaseFactory dbFactory = new();

        await dbFactory.Context.InsertAsync(new KubernetesInventoryProjectionCursor { Id = 1, Position = 10, UpdatedAt = FirstSeenAt });

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await dbFactory.Context.InsertAsync(new KubernetesInventoryProjectionCursor { Id = 1, Position = 20, UpdatedAt = FirstSeenAt }));
    }

    [Test]
    public async Task Cluster_ReportingColumns_RoundTripAllFields()
    {
        using TestDatabaseFactory dbFactory = new();

        DateTimeOffset snapshotAt = FirstSeenAt.AddHours(3);
        DateTimeOffset leaseCreatedAt = FirstSeenAt.AddDays(-2);

        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = 7,
            CaFingerprint = Fingerprint,
            Distro = "k3s",
            FirstSeenAt = FirstSeenAt,
            LastSeenAt = FirstSeenAt,
            KubeSystemUid = KubeSystemUid,
            ClusterVersion = "v1.31.2+k3s1",
            ReporterError = "credentials_rejected",
            RoleOutdated = true,
            Truncated = true,
            LeaseTransitions = 5_000_000_000L,
            ActiveReporterMachineId = 42,
            LastSnapshotReceivedAt = snapshotAt,
            LastSnapshotTelemetryId = 9_876_543_210L,
            LeaseUid = LeaseUid,
            LeaseCreatedAt = leaseCreatedAt,
        });

        KubernetesCluster cluster = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id == clusterId);

        await Assert.That(cluster.KubeSystemUid).IsEqualTo(KubeSystemUid);
        await Assert.That(cluster.ClusterVersion).IsEqualTo("v1.31.2+k3s1");
        await Assert.That(cluster.ReporterError).IsEqualTo("credentials_rejected");
        await Assert.That(cluster.RoleOutdated).IsTrue();
        await Assert.That(cluster.Truncated).IsTrue();
        await Assert.That(cluster.LeaseTransitions).IsEqualTo(5_000_000_000L);
        await Assert.That(cluster.ActiveReporterMachineId).IsEqualTo(42L);
        await Assert.That(cluster.LastSnapshotReceivedAt).IsEqualTo(snapshotAt);
        await Assert.That(cluster.LastSnapshotTelemetryId).IsEqualTo(9_876_543_210L);
        await Assert.That(cluster.LeaseUid).IsEqualTo(LeaseUid);
        await Assert.That(cluster.LeaseCreatedAt).IsEqualTo(leaseCreatedAt);
    }

    [Test]
    public async Task Cluster_ReportingColumnsLeftUnset_ReadBackAsDefaults()
    {
        // A cluster that no reporter has described yet must carry no reporting state: the nullable
        // columns are null and the flags and counter start at false and zero.
        using TestDatabaseFactory dbFactory = new();

        await dbFactory.Context.ExecuteAsync(
            """
            INSERT INTO "KubernetesClusters" ("TenantId", "CaFingerprint", "Distro", "FirstSeenAt", "LastSeenAt")
            VALUES (1, @fingerprint, 'k3s', @seenAt, @seenAt)
            """,
            new DataParameter("fingerprint", Fingerprint),
            new DataParameter("seenAt", FirstSeenAt.ToString("o")));

        KubernetesCluster cluster = await dbFactory.Context.KubernetesClusters.SingleAsync();

        await Assert.That(cluster.KubeSystemUid).IsNull();
        await Assert.That(cluster.ClusterVersion).IsNull();
        await Assert.That(cluster.ReporterError).IsNull();
        await Assert.That(cluster.RoleOutdated).IsFalse();
        await Assert.That(cluster.Truncated).IsFalse();
        await Assert.That(cluster.LeaseTransitions).IsEqualTo(0L);
        await Assert.That(cluster.ActiveReporterMachineId).IsNull();
        await Assert.That(cluster.LastSnapshotReceivedAt).IsNull();
        await Assert.That(cluster.LastSnapshotTelemetryId).IsNull();
        await Assert.That(cluster.LeaseUid).IsNull();
        await Assert.That(cluster.LeaseCreatedAt).IsNull();
    }

    [Test]
    public async Task Node_ReportingColumns_RoundTripAllFields()
    {
        using TestDatabaseFactory dbFactory = new();

        DateTimeOffset readySince = FirstSeenAt.AddMinutes(10);
        DateTimeOffset unschedulableSince = FirstSeenAt.AddMinutes(20);
        DateTimeOffset reporterSeenAt = FirstSeenAt.AddMinutes(30);
        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));

        long nodeId = await dbFactory.Context.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = 1,
            ClusterId = clusterId,
            NodeName = "node-a",
            NodeMachineId = "0123456789abcdef0123456789abcdef",
            Roles = RolesJson,
            ReadyStatus = "True",
            ReadySince = readySince,
            Conditions = ConditionsJson,
            Unschedulable = true,
            UnschedulableSince = unschedulableSince,
            ContainerRuntimeVersion = "containerd://1.7.23-k3s2",
            PodCount = 17,
            PodCapacity = 110,
            LastReporterSeenAt = reporterSeenAt,
        });

        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync(n => n.Id == nodeId);

        await Assert.That(node.NodeMachineId).IsEqualTo("0123456789abcdef0123456789abcdef");
        await Assert.That(node.Roles).IsEqualTo(RolesJson);
        await Assert.That(node.ReadyStatus).IsEqualTo("True");
        await Assert.That(node.ReadySince).IsEqualTo(readySince);
        await Assert.That(node.Conditions).IsEqualTo(ConditionsJson);
        await Assert.That(node.Unschedulable).IsTrue();
        await Assert.That(node.UnschedulableSince).IsEqualTo(unschedulableSince);
        await Assert.That(node.ContainerRuntimeVersion).IsEqualTo("containerd://1.7.23-k3s2");
        await Assert.That(node.PodCount).IsEqualTo(17);
        await Assert.That(node.PodCapacity).IsEqualTo(110);
        await Assert.That(node.LastReporterSeenAt).IsEqualTo(reporterSeenAt);
    }

    [Test]
    public async Task Node_ConditionsLongerThanAnyStringBound_RoundTripUnchanged()
    {
        // Roles and Conditions are JSON documents of no fixed length, so they must be stored as
        // unbounded text rather than a sized string.
        using TestDatabaseFactory dbFactory = new();

        string longConditions = "[" + string.Join(",", Enumerable.Range(0, 200)
            .Select(i => $$"""{"type":"Condition{{i}}","status":"False","lastTransitionTime":"2026-10-11T08:00:00Z"}""")) + "]";
        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));

        await dbFactory.Context.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = 1,
            ClusterId = clusterId,
            NodeName = "node-a",
            Conditions = longConditions,
        });

        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();

        await Assert.That(longConditions.Length).IsGreaterThan(8000);
        await Assert.That(node.Conditions).IsEqualTo(longConditions);
    }

    [Test]
    public async Task Node_ReportingColumnsLeftUnset_ReadBackAsDefaults()
    {
        // Nodes projected from a local report alone have nothing from a cluster snapshot yet.
        using TestDatabaseFactory dbFactory = new();

        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));

        await dbFactory.Context.ExecuteAsync(
            """INSERT INTO "KubernetesNodes" ("TenantId", "ClusterId", "NodeName") VALUES (1, @clusterId, 'node-a')""",
            new DataParameter("clusterId", clusterId));

        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();

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
    }

    [Test]
    public async Task Clusters_SameTenantAndKubeSystemUid_ThrowsUniqueConstraintViolation()
    {
        // The kube-system namespace UID identifies one cluster, so a tenant cannot hold it twice.
        using TestDatabaseFactory dbFactory = new();

        await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint, kubeSystemUid: KubeSystemUid));

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: new string('a', 64), kubeSystemUid: KubeSystemUid)));
    }

    [Test]
    public async Task Clusters_SameKubeSystemUidInDifferentTenants_IsAllowed()
    {
        // Tenants never share a cluster row, so two tenants reporting the same UID is not a conflict.
        using TestDatabaseFactory dbFactory = new();

        await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint, kubeSystemUid: KubeSystemUid));
        long otherTenantClusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 2, fingerprint: Fingerprint, kubeSystemUid: KubeSystemUid));

        await Assert.That(otherTenantClusterId).IsNotEqualTo(0L);
    }

    [Test]
    public async Task Clusters_NullKubeSystemUidInSameTenant_NeverConflicts()
    {
        // Clusters that only local reports have described have no UID yet, and any number of them
        // can exist in a tenant at once.
        using TestDatabaseFactory dbFactory = new();

        await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));
        await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: new string('a', 64)));
        await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: new string('b', 64), kubeSystemUid: KubeSystemUid));

        int withoutUid = await dbFactory.Context.KubernetesClusters.CountAsync(c => c.KubeSystemUid == null);

        await Assert.That(withoutUid).IsEqualTo(2);
    }

    [Test]
    public async Task ReporterCandidates_InsertAndRead_RoundTripsAllFields()
    {
        using TestDatabaseFactory dbFactory = new();

        DateTimeOffset lastReportAt = FirstSeenAt.AddMinutes(45);
        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 7, fingerprint: Fingerprint));

        await dbFactory.Context.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = 7,
            ClusterId = clusterId,
            MachineId = 42,
            ReporterError = "tls_ca_changed",
            LastReportAt = lastReportAt,
        });

        KubernetesReporterCandidate candidate = await dbFactory.Context.KubernetesReporterCandidates.SingleAsync();

        await Assert.That(candidate.TenantId).IsEqualTo(7);
        await Assert.That(candidate.ClusterId).IsEqualTo(clusterId);
        await Assert.That(candidate.MachineId).IsEqualTo(42L);
        await Assert.That(candidate.ReporterError).IsEqualTo("tls_ca_changed");
        await Assert.That(candidate.LastReportAt).IsEqualTo(lastReportAt);
    }

    [Test]
    public async Task ReporterCandidates_ReporterErrorLeftNull_ReadsBackAsNull()
    {
        using TestDatabaseFactory dbFactory = new();

        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));
        await dbFactory.Context.InsertAsync(BuildCandidate(clusterId, machineId: 1));

        KubernetesReporterCandidate candidate = await dbFactory.Context.KubernetesReporterCandidates.SingleAsync();

        await Assert.That(candidate.ReporterError).IsNull();
    }

    [Test]
    public async Task ReporterCandidates_SeveralMachinesInOneCluster_AreAllowed()
    {
        // Every eligible machine of a cluster is a candidate, and one of them holds the lease.
        using TestDatabaseFactory dbFactory = new();

        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));
        await dbFactory.Context.InsertAsync(BuildCandidate(clusterId, machineId: 1));
        await dbFactory.Context.InsertAsync(BuildCandidate(clusterId, machineId: 2));

        int candidates = await dbFactory.Context.KubernetesReporterCandidates.CountAsync(c => c.ClusterId == clusterId);

        await Assert.That(candidates).IsEqualTo(2);
    }

    [Test]
    public async Task ReporterCandidates_SameMachineInTwoClusters_ThrowsUniqueConstraintViolation()
    {
        // A machine reports for one cluster only, so the projection can move its candidate row but
        // never leave the machine listed under two clusters.
        using TestDatabaseFactory dbFactory = new();

        long firstClusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));
        long secondClusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: new string('a', 64)));
        await dbFactory.Context.InsertAsync(BuildCandidate(firstClusterId, machineId: 5));

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await dbFactory.Context.InsertAsync(BuildCandidate(secondClusterId, machineId: 5)));
    }

    [Test]
    public async Task ReporterCandidates_SameClusterAndMachineTwice_ThrowsUniqueConstraintViolation()
    {
        using TestDatabaseFactory dbFactory = new();

        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(BuildCluster(tenantId: 1, fingerprint: Fingerprint));
        await dbFactory.Context.InsertAsync(BuildCandidate(clusterId, machineId: 5));

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await dbFactory.Context.InsertAsync(BuildCandidate(clusterId, machineId: 5)));
    }

    private static KubernetesCluster BuildCluster(int tenantId, string fingerprint, string? kubeSystemUid = null)
    {
        return new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = fingerprint,
            Distro = "kubeadm",
            FirstSeenAt = FirstSeenAt,
            LastSeenAt = FirstSeenAt,
            KubeSystemUid = kubeSystemUid,
        };
    }

    private static KubernetesReporterCandidate BuildCandidate(long clusterId, long machineId)
    {
        return new KubernetesReporterCandidate
        {
            TenantId = 1,
            ClusterId = clusterId,
            MachineId = machineId,
            LastReportAt = FirstSeenAt,
        };
    }

    private static KubernetesNode BuildNode(long clusterId, string nodeName, long? machineId)
    {
        return new KubernetesNode
        {
            TenantId = 1,
            ClusterId = clusterId,
            NodeName = nodeName,
            MachineId = machineId,
        };
    }
}
