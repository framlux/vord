// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for the read and rename methods the Kubernetes web API uses on
/// <see cref="Database.Repositories.DatabaseRepository"/>. Every method is scoped to one tenant, so
/// each test seeds a second tenant that must never show through.
/// </summary>
public class KubernetesClusterQueryRepositoryTests
{
    private const string FingerprintA = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";
    private const string FingerprintB = "0f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a6978";

    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static Database.Repositories.DatabaseRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new Database.Repositories.DatabaseRepository(
            dbFactory.Context, NullLogger<Database.Repositories.DatabaseRepository>.Instance);
    }

    private static async Task<int> SeedTenantAsync(DatabaseContext db)
    {
        return await db.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant());
    }

    private static async Task<long> SeedMachineAsync(DatabaseContext db, int tenantId, string name)
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId, hostname: name);

        return await db.InsertWithInt64IdentityAsync(machine);
    }

    private static async Task<long> SeedClusterAsync(
        DatabaseContext db, int tenantId, string fingerprint, string? displayName = null, string distro = "k3s")
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = fingerprint,
            DisplayName = displayName,
            Distro = distro,
            FirstSeenAt = T0,
            LastSeenAt = T0,
        });
    }

    private static async Task<long> SeedNodeAsync(
        DatabaseContext db, int tenantId, long clusterId, string nodeName, long? machineId = null)
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = nodeName,
            MachineId = machineId,
            LastLocalReportAt = T0,
        });
    }

    [Test]
    public async Task ListClusters_ReturnsOnlyTheTenantsClustersInIdOrder()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long first = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA);
        long second = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB);

        IReadOnlyList<KubernetesCluster> clusters = await repo.ListClustersAsync(tenantId, CancellationToken.None);

        await Assert.That(clusters.Count).IsEqualTo(2);
        await Assert.That(clusters[0].Id).IsEqualTo(first);
        await Assert.That(clusters[1].Id).IsEqualTo(second);
        await Assert.That(clusters.All(c => c.TenantId == tenantId)).IsTrue();
    }

    [Test]
    public async Task ListClusters_TenantWithoutClusters_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA);

        IReadOnlyList<KubernetesCluster> clusters = await repo.ListClustersAsync(tenantId, CancellationToken.None);

        await Assert.That(clusters.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetCluster_OwnCluster_ReturnsIt()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "prod");

        KubernetesCluster? cluster = await repo.GetClusterAsync(tenantId, clusterId, CancellationToken.None);

        await Assert.That(cluster).IsNotNull();
        await Assert.That(cluster!.Id).IsEqualTo(clusterId);
        await Assert.That(cluster.DisplayName).IsEqualTo("prod");
        await Assert.That(cluster.CaFingerprint).IsEqualTo(FingerprintA);
    }

    [Test]
    public async Task GetCluster_AnotherTenantsClusterOrUnknownId_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long foreignClusterId = await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA);

        KubernetesCluster? foreign = await repo.GetClusterAsync(tenantId, foreignClusterId, CancellationToken.None);
        KubernetesCluster? unknown = await repo.GetClusterAsync(tenantId, 987654, CancellationToken.None);

        await Assert.That(foreign).IsNull();
        await Assert.That(unknown).IsNull();
    }

    [Test]
    public async Task ListNodes_ReturnsNodesOfTheRequestedClustersWithTheLinkedMachineName()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-host");
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long otherClusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB);
        long managedNodeId = await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", machineId);
        long unmanagedNodeId = await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "worker-1");
        await SeedNodeAsync(dbFactory.Context, tenantId, otherClusterId, "elsewhere");

        IReadOnlyList<KubernetesNodeWithMachine> nodes = await repo.ListNodesAsync(tenantId, [clusterId], CancellationToken.None);

        await Assert.That(nodes.Count).IsEqualTo(2);
        KubernetesNodeWithMachine managed = nodes.Single(n => n.Node.Id == managedNodeId);
        await Assert.That(managed.Node.NodeName).IsEqualTo("cp-1");
        await Assert.That(managed.Node.MachineId).IsEqualTo(machineId);
        await Assert.That(managed.MachineName).IsEqualTo("cp-host");
        KubernetesNodeWithMachine unmanaged = nodes.Single(n => n.Node.Id == unmanagedNodeId);
        await Assert.That(unmanaged.Node.MachineId).IsNull();
        await Assert.That(unmanaged.MachineName).IsNull();
    }

    [Test]
    public async Task ListNodes_SeveralClusters_ReturnsTheNodesOfEach()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterA = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long clusterB = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterA, "a-1");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterB, "b-1");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterB, "b-2");

        IReadOnlyList<KubernetesNodeWithMachine> nodes = await repo.ListNodesAsync(tenantId, [clusterA, clusterB], CancellationToken.None);

        await Assert.That(nodes.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ListNodes_NoClusterIds_ReturnsEmptyWithoutQuerying()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "a-1");

        IReadOnlyList<KubernetesNodeWithMachine> nodes = await repo.ListNodesAsync(tenantId, [], CancellationToken.None);

        await Assert.That(nodes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListNodes_ClusterOfAnotherTenant_ReturnsNothing()
    {
        // Intent: asking for another tenant's cluster id must not reveal its nodes or the names of the
        // machines linked to them, however the id was obtained.
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long otherMachineId = await SeedMachineAsync(dbFactory.Context, otherTenantId, "secret-host");
        long otherClusterId = await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA);
        await SeedNodeAsync(dbFactory.Context, otherTenantId, otherClusterId, "secret-node", otherMachineId);

        IReadOnlyList<KubernetesNodeWithMachine> nodes = await repo.ListNodesAsync(tenantId, [otherClusterId], CancellationToken.None);

        await Assert.That(nodes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetNodeForMachine_LinkedMachine_ReturnsItsNode()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-host");
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long nodeId = await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", machineId);

        KubernetesNode? node = await repo.GetNodeForMachineAsync(tenantId, machineId, CancellationToken.None);

        await Assert.That(node).IsNotNull();
        await Assert.That(node!.Id).IsEqualTo(nodeId);
        await Assert.That(node.ClusterId).IsEqualTo(clusterId);
    }

    [Test]
    public async Task GetNodeForMachine_UnlinkedMachineOrOtherTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long unlinkedMachineId = await SeedMachineAsync(dbFactory.Context, tenantId, "plain-host");
        long otherMachineId = await SeedMachineAsync(dbFactory.Context, otherTenantId, "other-host");
        long otherClusterId = await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA);
        await SeedNodeAsync(dbFactory.Context, otherTenantId, otherClusterId, "other-1", otherMachineId);

        KubernetesNode? unlinked = await repo.GetNodeForMachineAsync(tenantId, unlinkedMachineId, CancellationToken.None);
        KubernetesNode? foreign = await repo.GetNodeForMachineAsync(tenantId, otherMachineId, CancellationToken.None);

        await Assert.That(unlinked).IsNull();
        await Assert.That(foreign).IsNull();
    }

    [Test]
    public async Task ListClustersByFingerprint_ReturnsEveryOwnClusterHoldingIt()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long first = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long second = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB);
        await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA);

        IReadOnlyList<KubernetesCluster> clusters = await repo.ListClustersByFingerprintAsync(tenantId, FingerprintA, CancellationToken.None);

        await Assert.That(clusters.Count).IsEqualTo(2);
        await Assert.That(clusters[0].Id).IsEqualTo(first);
        await Assert.That(clusters[1].Id).IsEqualTo(second);
    }

    [Test]
    public async Task ListClustersByFingerprint_UnheldFingerprint_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);

        IReadOnlyList<KubernetesCluster> clusters = await repo.ListClustersByFingerprintAsync(tenantId, FingerprintB, CancellationToken.None);

        await Assert.That(clusters.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RenameCluster_SetsTheDisplayNameOfTheTenantsCluster()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);

        bool renamed = await RenameInTransactionAsync(repo, tenantId, clusterId, "Production");

        await Assert.That(renamed).IsTrue();
        KubernetesCluster stored = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
        await Assert.That(stored.DisplayName).IsEqualTo("Production");
    }

    [Test]
    public async Task RenameCluster_NullName_ResetsToTheDerivedName()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "Production");

        bool renamed = await RenameInTransactionAsync(repo, tenantId, clusterId, null);

        await Assert.That(renamed).IsTrue();
        KubernetesCluster stored = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
        await Assert.That(stored.DisplayName).IsNull();
    }

    [Test]
    public async Task RenameCluster_AnotherTenantsClusterOrUnknownId_ChangesNothingAndReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long foreignClusterId = await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA, displayName: "Theirs");

        bool foreign = await RenameInTransactionAsync(repo, tenantId, foreignClusterId, "Mine now");
        bool unknown = await RenameInTransactionAsync(repo, tenantId, 987654, "Nothing");

        await Assert.That(foreign).IsFalse();
        await Assert.That(unknown).IsFalse();
        KubernetesCluster stored = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id == foreignClusterId);
        await Assert.That(stored.DisplayName).IsEqualTo("Theirs");
    }

    private static async Task<bool> RenameInTransactionAsync(
        Database.Repositories.DatabaseRepository repo, int tenantId, long clusterId, string? displayName)
    {
        using IDatabaseTransaction transaction = await ((IDatabaseTransactionProvider)repo).BeginTransactionAsync(CancellationToken.None);
        bool renamed = await ((IKubernetesInventoryRepository)repo).RenameClusterAsync(tenantId, clusterId, displayName, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return renamed;
    }
}
