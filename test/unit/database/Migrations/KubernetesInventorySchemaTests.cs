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
/// belong to, the nodes inside them, and the cursor the inventory projection resumes from.
/// </summary>
public class KubernetesInventorySchemaTests
{
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

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

    private static KubernetesCluster BuildCluster(int tenantId, string fingerprint)
    {
        return new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = fingerprint,
            Distro = "kubeadm",
            FirstSeenAt = FirstSeenAt,
            LastSeenAt = FirstSeenAt,
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
