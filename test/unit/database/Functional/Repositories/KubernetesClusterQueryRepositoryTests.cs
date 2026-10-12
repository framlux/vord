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
/// Functional tests for the read and rename methods the Kubernetes web API and the cluster reporter
/// command check use on <see cref="Database.Repositories.DatabaseRepository"/>. Every method is scoped
/// to one tenant, so each test seeds a second tenant that must never show through.
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

    [Test]
    public async Task GetCandidateForMachine_CandidateOfTheTenant_ReturnsItsRow()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-1");
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await dbFactory.Context.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            MachineId = machineId,
            ReporterError = "credentials_rejected",
            LastReportAt = T0,
        });

        KubernetesReporterCandidate? candidate = await repo.GetCandidateForMachineAsync(tenantId, machineId, CancellationToken.None);

        await Assert.That(candidate).IsNotNull();
        await Assert.That(candidate!.ClusterId).IsEqualTo(clusterId);
        await Assert.That(candidate.MachineId).IsEqualTo(machineId);
        await Assert.That(candidate.ReporterError).IsEqualTo("credentials_rejected");
    }

    [Test]
    public async Task GetCandidateForMachine_NotACandidateOrAnotherTenantsRow_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long plainMachineId = await SeedMachineAsync(dbFactory.Context, tenantId, "plain");
        long foreignMachineId = await SeedMachineAsync(dbFactory.Context, otherTenantId, "foreign");
        long foreignClusterId = await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA);
        await dbFactory.Context.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = otherTenantId,
            ClusterId = foreignClusterId,
            MachineId = foreignMachineId,
            LastReportAt = T0,
        });

        KubernetesReporterCandidate? plain = await repo.GetCandidateForMachineAsync(tenantId, plainMachineId, CancellationToken.None);
        KubernetesReporterCandidate? foreign = await repo.GetCandidateForMachineAsync(tenantId, foreignMachineId, CancellationToken.None);

        await Assert.That(plain).IsNull();
        await Assert.That(foreign).IsNull();
    }

    [Test]
    public async Task ListCandidates_ReturnsTheCandidatesOfTheRequestedClustersInClusterThenMachineOrder()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineA = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-a");
        long machineB = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-b");
        long machineC = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-c");
        long first = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long second = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB);
        long unrelated = await SeedClusterAsync(dbFactory.Context, tenantId, "c" + FingerprintA[1..]);
        await dbFactory.Context.InsertAsync(new KubernetesReporterCandidate { TenantId = tenantId, ClusterId = second, MachineId = machineC, LastReportAt = T0 });
        await dbFactory.Context.InsertAsync(new KubernetesReporterCandidate { TenantId = tenantId, ClusterId = first, MachineId = machineB, ReporterError = "tls_ca_changed", LastReportAt = T0 });
        await dbFactory.Context.InsertAsync(new KubernetesReporterCandidate { TenantId = tenantId, ClusterId = first, MachineId = machineA, LastReportAt = T0.AddMinutes(1) });
        await dbFactory.Context.InsertAsync(new KubernetesReporterCandidate { TenantId = tenantId, ClusterId = unrelated, MachineId = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-d"), LastReportAt = T0 });

        IReadOnlyList<KubernetesReporterCandidate> candidates = await repo.ListCandidatesAsync(tenantId, [first, second], CancellationToken.None);

        await Assert.That(candidates.Select(c => (c.ClusterId, c.MachineId)).ToArray())
            .IsEquivalentTo(new[] { (first, machineA), (first, machineB), (second, machineC) });
        await Assert.That(candidates[0].MachineId).IsEqualTo(machineA);
        await Assert.That(candidates[1].ReporterError).IsEqualTo("tls_ca_changed");
        await Assert.That(candidates[2].ClusterId).IsEqualTo(second);
    }

    [Test]
    public async Task ListCandidates_ClusterOfAnotherTenantOrNoClusterIds_ReturnsNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long foreignMachine = await SeedMachineAsync(dbFactory.Context, otherTenantId, "foreign");
        long foreignCluster = await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA);
        await dbFactory.Context.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = otherTenantId,
            ClusterId = foreignCluster,
            MachineId = foreignMachine,
            LastReportAt = T0,
        });

        IReadOnlyList<KubernetesReporterCandidate> foreign = await repo.ListCandidatesAsync(tenantId, [foreignCluster], CancellationToken.None);
        IReadOnlyList<KubernetesReporterCandidate> none = await repo.ListCandidatesAsync(tenantId, [], CancellationToken.None);

        await Assert.That(foreign.Count).IsEqualTo(0);
        await Assert.That(none.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetMachineNamesInTenant_ReturnsTheNamesOfTheTenantsMachinesOnly()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long own = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-own");
        long ownToo = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-two");
        long notAsked = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-quiet");
        long foreign = await SeedMachineAsync(dbFactory.Context, otherTenantId, "cp-foreign");

        IReadOnlyDictionary<long, string> names = await repo.GetMachineNamesInTenantAsync(
            tenantId, [own, ownToo, foreign, 987654], CancellationToken.None);

        await Assert.That(names.Count).IsEqualTo(2);
        await Assert.That(names[own]).IsEqualTo("cp-own");
        await Assert.That(names[ownToo]).IsEqualTo("cp-two");
        await Assert.That(names.ContainsKey(foreign)).IsFalse();
        await Assert.That(names.ContainsKey(notAsked)).IsFalse();
    }

    [Test]
    public async Task GetMachineNamesInTenant_NoMachineIds_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);

        IReadOnlyDictionary<long, string> names = await repo.GetMachineNamesInTenantAsync(tenantId, [], CancellationToken.None);

        await Assert.That(names.Count).IsEqualTo(0);
    }

    private static async Task InsertKubernetesTelemetryAsync(
        DatabaseContext db, int tenantId, long machineId, short type, string payload, DateTimeOffset receivedAt, DateTimeOffset serverReceivedAt)
    {
        await db.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachineTelemetry(
            machineId: machineId, tenantId: tenantId, telemetryType: type, payload: payload,
            receivedAt: receivedAt, serverReceivedAt: serverReceivedAt));
    }

    [Test]
    public async Task GetLatestKubernetesNodePayloads_ReturnsTheLatestTypeFourteenPayloadOfEachMachine()
    {
        // Intent: one read answers for several machines at once and yields the newest local report of
        // each by server receipt time, whatever order the rows were written in.
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long first = await SeedMachineAsync(db, tenantId, "first");
        long second = await SeedMachineAsync(db, tenantId, "second");
        long silent = await SeedMachineAsync(db, tenantId, "silent");
        await InsertKubernetesTelemetryAsync(db, tenantId, first, 14, "first-newest", T0.AddMinutes(-1), T0.AddMinutes(-1));
        await InsertKubernetesTelemetryAsync(db, tenantId, first, 14, "first-older", T0.AddMinutes(-30), T0.AddMinutes(-30));
        await InsertKubernetesTelemetryAsync(db, tenantId, second, 14, "second-oldest", T0.AddHours(-3), T0.AddHours(-3));
        await InsertKubernetesTelemetryAsync(db, tenantId, second, 14, "second-newest", T0.AddHours(-2), T0.AddHours(-2));
        await InsertKubernetesTelemetryAsync(db, tenantId, second, 14, "second-middle", T0.AddHours(-2).AddMinutes(-5), T0.AddHours(-2).AddMinutes(-5));

        IReadOnlyDictionary<long, string> payloads = await repo.GetLatestKubernetesNodePayloadsAsync(
            tenantId, [first, second, silent], T0.AddDays(-7), CancellationToken.None);

        await Assert.That(payloads.Count).IsEqualTo(2);
        await Assert.That(payloads[first]).IsEqualTo("first-newest");
        await Assert.That(payloads[second]).IsEqualTo("second-newest");
        await Assert.That(payloads.ContainsKey(silent)).IsFalse();
    }

    [Test]
    public async Task GetLatestKubernetesNodePayloads_IgnoresOtherTypesOtherMachinesOtherTenantsAndRowsOutsideTheWindow()
    {
        // Intent: only a type-14 row of an asked-for machine of this tenant, received inside the window,
        // can answer. A newer cluster snapshot or CPU row must not shadow the local report, and a row of
        // another tenant that carries the same machine id must never show through.
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        int otherTenantId = await SeedTenantAsync(db);
        long asked = await SeedMachineAsync(db, tenantId, "asked");
        long notAsked = await SeedMachineAsync(db, tenantId, "not-asked");
        long expired = await SeedMachineAsync(db, tenantId, "expired");
        DateTimeOffset receivedAfter = T0.AddDays(-7);
        await InsertKubernetesTelemetryAsync(db, tenantId, asked, 14, "local-report", T0.AddMinutes(-10), T0.AddMinutes(-10));
        await InsertKubernetesTelemetryAsync(db, tenantId, asked, 15, "cluster-snapshot", T0.AddMinutes(-1), T0.AddMinutes(-1));
        await InsertKubernetesTelemetryAsync(db, tenantId, asked, 1, "cpu", T0.AddMinutes(-1), T0.AddMinutes(-1));
        await InsertKubernetesTelemetryAsync(db, otherTenantId, asked, 14, "foreign-report", T0.AddMinutes(-1), T0.AddMinutes(-1));
        await InsertKubernetesTelemetryAsync(db, tenantId, notAsked, 14, "not-asked-report", T0.AddMinutes(-1), T0.AddMinutes(-1));
        await InsertKubernetesTelemetryAsync(db, tenantId, expired, 14, "expired-report", receivedAfter, receivedAfter);

        IReadOnlyDictionary<long, string> payloads = await repo.GetLatestKubernetesNodePayloadsAsync(
            tenantId, [asked, expired], receivedAfter, CancellationToken.None);

        await Assert.That(payloads.Count).IsEqualTo(1);
        await Assert.That(payloads[asked]).IsEqualTo("local-report");
    }

    [Test]
    public async Task GetLatestKubernetesNodePayloads_WindowAndOrderFollowTheServersClockNotTheAgents()
    {
        // Intent: ReceivedAt comes from the agent's own clock, so it says nothing about how recent a
        // report is. A machine whose clock runs slow, so that ReceivedAt is old but the server accepted
        // the report a moment ago, must still be found; one whose clock runs fast, so that ReceivedAt
        // is fresh but the server accepted the report long ago, must not be; and of two reports the
        // one the server accepted last wins whatever the agent's timestamps say.
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long slowClock = await SeedMachineAsync(db, tenantId, "slow-clock");
        long fastClock = await SeedMachineAsync(db, tenantId, "fast-clock");
        long reordered = await SeedMachineAsync(db, tenantId, "reordered");
        DateTimeOffset serverReceivedAfter = T0.AddDays(-7);
        await InsertKubernetesTelemetryAsync(
            db, tenantId, slowClock, 14, "slow-clock-report", receivedAt: serverReceivedAfter.AddHours(-1), serverReceivedAt: T0.AddMinutes(-1));
        await InsertKubernetesTelemetryAsync(
            db, tenantId, fastClock, 14, "fast-clock-report", receivedAt: T0, serverReceivedAt: serverReceivedAfter.AddHours(-1));
        await InsertKubernetesTelemetryAsync(
            db, tenantId, reordered, 14, "accepted-first-but-stamped-later", receivedAt: T0, serverReceivedAt: T0.AddMinutes(-30));
        await InsertKubernetesTelemetryAsync(
            db, tenantId, reordered, 14, "accepted-last-but-stamped-earlier", receivedAt: T0.AddHours(-3), serverReceivedAt: T0.AddMinutes(-1));

        IReadOnlyDictionary<long, string> payloads = await repo.GetLatestKubernetesNodePayloadsAsync(
            tenantId, [slowClock, fastClock, reordered], serverReceivedAfter, CancellationToken.None);

        await Assert.That(payloads.Count).IsEqualTo(2);
        await Assert.That(payloads[slowClock]).IsEqualTo("slow-clock-report");
        await Assert.That(payloads.ContainsKey(fastClock)).IsFalse();
        await Assert.That(payloads[reordered]).IsEqualTo("accepted-last-but-stamped-earlier");
    }

    [Test]
    public async Task GetLatestKubernetesNodePayloads_ReportsAcceptedAtTheSameInstant_TheLaterRowWins()
    {
        // Intent: "latest" must be deterministic. When two reports of one machine were accepted at the
        // same instant, the one stored last, with the higher id, is the answer, in whatever order
        // the rows come back.
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;
        int tenantId = await SeedTenantAsync(db);
        long machineId = await SeedMachineAsync(db, tenantId, "tie");
        await InsertKubernetesTelemetryAsync(db, tenantId, machineId, 14, "stored-first", T0.AddMinutes(-2), T0.AddMinutes(-1));
        await InsertKubernetesTelemetryAsync(db, tenantId, machineId, 14, "stored-last", T0.AddMinutes(-3), T0.AddMinutes(-1));
        await InsertKubernetesTelemetryAsync(db, tenantId, machineId, 14, "stored-before-the-tie", T0.AddMinutes(-9), T0.AddMinutes(-9));

        IReadOnlyDictionary<long, string> payloads = await repo.GetLatestKubernetesNodePayloadsAsync(
            tenantId, [machineId], T0.AddDays(-7), CancellationToken.None);

        await Assert.That(payloads[machineId]).IsEqualTo("stored-last");
    }

    [Test]
    public async Task GetLatestKubernetesNodePayloads_NoMachineIds_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);

        IReadOnlyDictionary<long, string> payloads = await repo.GetLatestKubernetesNodePayloadsAsync(
            tenantId, [], T0.AddDays(-7), CancellationToken.None);

        await Assert.That(payloads.Count).IsEqualTo(0);
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
