// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for machine-state-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class MachineStateRepositoryTests
{
    /// <summary>
    /// Seeds a user, tenant, machine, and initial summary/detail rows for update-oriented tests.
    /// </summary>
    private static async Task<(int userId, int tenantId, long machineId)> SeedMachineWithStateAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);
        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        // Insert summary and detail rows (these are required for update tests)
        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: tenantId);
        await dbFactory.Context.InsertAsync(summary);
        MachineStateDetail detail = new() { MachineId = machineId };
        await dbFactory.Context.InsertAsync(detail);

        return (userId, tenantId, machineId);
    }

    // ========== InsertSummaryAsync tests ==========

    [Test]
    public async Task InsertSummaryAsync_ValidSummary_InsertsAndIsRetrievable()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(machineId: 100, tenantId: 1, name: "test-box");
        await repo.InsertSummaryAsync(summary);

        MachineStateSummary? retrieved = await repo.GetSummaryForMachineAsync(100);

        await Assert.That(retrieved).IsNotNull();
        await Assert.That(retrieved!.MachineId).IsEqualTo(100L);
        await Assert.That(retrieved.Name).IsEqualTo("test-box");
        await Assert.That(retrieved.TenantId).IsEqualTo(1);
    }

    [Test]
    public async Task InsertSummaryAsync_NullInput_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.InsertSummaryAsync(null!)).Throws<ArgumentNullException>();
    }

    // ========== InsertDetailAsync tests ==========

    [Test]
    public async Task InsertDetailAsync_ValidDetail_InsertsSuccessfully()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        MachineStateDetail detail = new() { MachineId = 200, CpuBrand = "Intel Xeon" };
        await repo.InsertDetailAsync(detail);

        // Verify by reading back through the context directly
        MachineStateDetail? retrieved = await dbFactory.Context.MachineStateDetails
            .Where(d => d.MachineId == 200)
            .FirstOrDefaultAsync();

        await Assert.That(retrieved).IsNotNull();
        await Assert.That(retrieved!.MachineId).IsEqualTo(200L);
        await Assert.That(retrieved.CpuBrand).IsEqualTo("Intel Xeon");
    }

    [Test]
    public async Task InsertDetailAsync_NullInput_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.InsertDetailAsync(null!)).Throws<ArgumentNullException>();
    }

    // ========== InsertTelemetryAsync tests ==========

    [Test]
    public async Task InsertTelemetryAsync_ValidRow_InsertsAndAssignsId()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        MachineTelemetry row = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 3);
        await repo.InsertTelemetryAsync(row);

        List<MachineTelemetry> all = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(all.Count).IsEqualTo(1);
        await Assert.That(all[0].TelemetryType).IsEqualTo((short)3);
    }

    [Test]
    public async Task InsertTelemetryAsync_NullInput_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.InsertTelemetryAsync(null!)).Throws<ArgumentNullException>();
    }

    // ========== BulkInsertTelemetryAsync tests ==========

    [Test]
    public async Task BulkInsertTelemetryAsync_MultipleRows_InsertsAll()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<MachineTelemetry> rows = new()
        {
            TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 1),
            TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 2),
            TestDataBuilder.BuildMachineTelemetry(machineId: 2, tenantId: 1, telemetryType: 1),
        };

        await repo.BulkInsertTelemetryAsync(rows);

        List<MachineTelemetry> all = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(all.Count).IsEqualTo(3);
    }

    [Test]
    public async Task BulkInsertTelemetryAsync_NullInput_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.BulkInsertTelemetryAsync(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task BulkInsertTelemetryAsync_EmptyList_InsertsNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await repo.BulkInsertTelemetryAsync(new List<MachineTelemetry>());

        List<MachineTelemetry> all = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(all.Count).IsEqualTo(0);
    }

    // ========== GetDistinctTenantIdsAsync tests ==========

    [Test]
    public async Task GetDistinctTenantIdsAsync_EmptyTable_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<int> result = await repo.GetDistinctTenantIdsAsync();

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetDistinctTenantIdsAsync_MultipleTenants_ReturnsDistinctIds()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 1, tenantId: 10));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 2, tenantId: 20));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 3, tenantId: 10));

        List<int> result = await repo.GetDistinctTenantIdsAsync();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result).Contains(10);
        await Assert.That(result).Contains(20);
    }

    // ========== GetSummaryForMachineAsync tests ==========

    [Test]
    public async Task GetSummaryForMachineAsync_ExistingSummary_ReturnsSummary()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 42, tenantId: 1, name: "found-box"));

        MachineStateSummary? result = await repo.GetSummaryForMachineAsync(42);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.MachineId).IsEqualTo(42L);
        await Assert.That(result.Name).IsEqualTo("found-box");
    }

    [Test]
    public async Task GetSummaryForMachineAsync_NoSummary_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        MachineStateSummary? result = await repo.GetSummaryForMachineAsync(99999);

        await Assert.That(result).IsNull();
    }

    // ========== GetSummariesForTenantMachinesAsync tests ==========

    [Test]
    public async Task GetSummariesForTenantMachinesAsync_WithActiveMachines_ReturnsSummaries()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int _, int tenantId, long machineId) = await SeedMachineWithStateAsync(dbFactory);

        List<MachineStateSummary> result = await repo.GetSummariesForTenantMachinesAsync(tenantId);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].MachineId).IsEqualTo(machineId);
    }

    [Test]
    public async Task GetSummariesForTenantMachinesAsync_EmptyTenant_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<MachineStateSummary> result = await repo.GetSummariesForTenantMachinesAsync(99999);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetSummariesForTenantMachines_FiltersByDenormalizedTenantId()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        // Summaries are filtered purely by their denormalized TenantId; no backing Machines rows
        // are needed for the query to return them.
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 1, tenantId: 7));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 2, tenantId: 7));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 3, tenantId: 9));

        List<MachineStateSummary> result = await repo.GetSummariesForTenantMachinesAsync(7);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.All(s => s.TenantId == 7)).IsTrue();
    }

    [Test]
    public async Task GetSummariesForTenantMachinesAsync_SoftDeletedMachine_SummaryRemovedAndExcluded()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Active machine with state summary.
        Machine activeMachine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long activeMachineId = await dbFactory.Context.InsertWithInt64IdentityAsync(activeMachine);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: activeMachineId, tenantId: tenantId));

        // Second machine with a summary that will be soft-deleted.
        Machine doomedMachine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long doomedMachineId = await dbFactory.Context.InsertWithInt64IdentityAsync(doomedMachine);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: doomedMachineId, tenantId: tenantId));

        string? deletedKeyHash = await repo.SoftDeleteMachineAsync(doomedMachineId, tenantId, userId, CancellationToken.None);

        await Assert.That(deletedKeyHash).IsNotNull();
        List<MachineStateSummary> result = await repo.GetSummariesForTenantMachinesAsync(tenantId, CancellationToken.None);
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].MachineId).IsEqualTo(activeMachineId);
    }

    // ========== SoftDeleteMachineAsync Kubernetes node tests ==========

    private static async Task<long> SeedKubernetesNodeAsync(
        TestDatabaseFactory dbFactory, int tenantId, string nodeName, long? machineId)
    {
        DateTimeOffset seenAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        long clusterId = await dbFactory.Context.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Distro = "k3s",
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
        });

        return await dbFactory.Context.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = nodeName,
            MachineId = machineId,
            LastLocalReportAt = seenAt,
        });
    }

    [Test]
    public async Task SoftDelete_UnlinksKubernetesNode()
    {
        // Intent: a soft delete only flips IsDeleted, so the foreign key from the node to the machine
        // never fires; the repository has to clear the link itself. The node row is inventory the
        // cluster still owns, so it survives with no machine rather than being deleted.
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildUser());
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: tenantId));
        long nodeId = await SeedKubernetesNodeAsync(dbFactory, tenantId, "node-1", machineId);

        string? deletedKeyHash = await repo.SoftDeleteMachineAsync(machineId, tenantId, userId, CancellationToken.None);

        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync(n => n.Id == nodeId);
        await Assert.That(deletedKeyHash).IsNotNull();
        await Assert.That(node.MachineId).IsNull();
        await Assert.That(node.NodeName).IsEqualTo("node-1");
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync(c => c.Id == node.ClusterId)).IsEqualTo(1);
    }

    [Test]
    public async Task SoftDelete_OtherTenantNodeUntouched()
    {
        // Intent: only the deleted machine's own node is unlinked. A node linked to another machine of
        // the same tenant, a node of another tenant linked to that tenant's machine, and a node of
        // another tenant that were ever to carry the deleted machine's id are all left as they were.
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildUser());
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        int otherTenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        long doomedMachineId = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: tenantId));
        long siblingMachineId = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: tenantId));
        long otherTenantMachineId = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: otherTenantId));
        long siblingNodeId = await SeedKubernetesNodeAsync(dbFactory, tenantId, "sibling", siblingMachineId);
        long otherTenantNodeId = await SeedKubernetesNodeAsync(dbFactory, otherTenantId, "other-tenant", otherTenantMachineId);
        long foreignNodeId = await SeedKubernetesNodeAsync(dbFactory, otherTenantId, "foreign", doomedMachineId);
        long ownNodeId = await SeedKubernetesNodeAsync(dbFactory, tenantId, "own", null);

        await repo.SoftDeleteMachineAsync(doomedMachineId, tenantId, userId, CancellationToken.None);

        Dictionary<long, long?> links = await dbFactory.Context.KubernetesNodes.ToDictionaryAsync(n => n.Id, n => n.MachineId);
        await Assert.That(links[siblingNodeId]).IsEqualTo(siblingMachineId);
        await Assert.That(links[otherTenantNodeId]).IsEqualTo(otherTenantMachineId);
        await Assert.That(links[foreignNodeId]).IsEqualTo(doomedMachineId);
        await Assert.That(links[ownNodeId]).IsNull();
    }

    [Test]
    public async Task SoftDelete_MachineOfAnotherTenant_LeavesNodeLinked()
    {
        // Intent: a delete that matches no active machine of the caller's tenant changes nothing, the
        // node link included, so a foreign machine id cannot be used to detach another tenant's node.
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildUser());
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        int otherTenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: tenantId));
        long nodeId = await SeedKubernetesNodeAsync(dbFactory, tenantId, "node-1", machineId);

        string? deletedKeyHash = await repo.SoftDeleteMachineAsync(machineId, otherTenantId, userId, CancellationToken.None);

        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync(n => n.Id == nodeId);
        await Assert.That(deletedKeyHash).IsNull();
        await Assert.That(node.MachineId).IsEqualTo(machineId);
    }

    private static async Task<long> SeedReportingClusterAsync(
        TestDatabaseFactory dbFactory, int tenantId, long? activeReporterMachineId)
    {
        DateTimeOffset seenAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        return await dbFactory.Context.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Distro = "k3s",
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
            ActiveReporterMachineId = activeReporterMachineId,
            LastSnapshotReceivedAt = activeReporterMachineId == null ? null : seenAt,
        });
    }

    private static async Task SeedKubernetesCandidateAsync(
        TestDatabaseFactory dbFactory, int tenantId, long clusterId, long machineId)
    {
        await dbFactory.Context.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            MachineId = machineId,
            LastReportAt = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero),
        });
    }

    [Test]
    public async Task SoftDelete_RemovesTheMachinesReporterCandidateOnly()
    {
        // Intent: a soft delete never fires the foreign key from a candidate row to its machine, so the
        // repository removes the row itself. Another machine's candidate in the same cluster, and a
        // candidate of another tenant, stay.
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildUser());
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        int otherTenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        long doomedMachineId = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: tenantId));
        long siblingMachineId = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: tenantId));
        long otherTenantMachineId = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: otherTenantId));
        long clusterId = await SeedReportingClusterAsync(dbFactory, tenantId, null);
        long otherClusterId = await SeedReportingClusterAsync(dbFactory, otherTenantId, null);
        await SeedKubernetesCandidateAsync(dbFactory, tenantId, clusterId, doomedMachineId);
        await SeedKubernetesCandidateAsync(dbFactory, tenantId, clusterId, siblingMachineId);
        await SeedKubernetesCandidateAsync(dbFactory, otherTenantId, otherClusterId, otherTenantMachineId);

        string? deletedKeyHash = await repo.SoftDeleteMachineAsync(doomedMachineId, tenantId, userId, CancellationToken.None);

        await Assert.That(deletedKeyHash).IsNotNull();
        List<long> remaining = await dbFactory.Context.KubernetesReporterCandidates.Select(c => c.MachineId).ToListAsync();
        await Assert.That(remaining).IsEquivalentTo(new List<long> { siblingMachineId, otherTenantMachineId });
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(2);
    }

    [Test]
    public async Task SoftDelete_ClearsTheActiveReporterOfClustersThatNameTheMachine()
    {
        // Intent: a cluster whose latest snapshot came from the deleted machine stops naming it as the
        // active reporter, while a cluster whose active reporter is another machine, and another
        // tenant's cluster that was ever to carry the same id, keep theirs. The snapshot markers stay,
        // because the snapshot itself is still the cluster's latest.
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildUser());
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        int otherTenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        long doomedMachineId = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: tenantId));
        long siblingMachineId = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: tenantId));
        long activeCluster = await SeedReportingClusterAsync(dbFactory, tenantId, doomedMachineId);
        long siblingCluster = await SeedReportingClusterAsync(dbFactory, tenantId, siblingMachineId);
        long foreignCluster = await SeedReportingClusterAsync(dbFactory, otherTenantId, doomedMachineId);

        await repo.SoftDeleteMachineAsync(doomedMachineId, tenantId, userId, CancellationToken.None);

        Dictionary<long, long?> active = await dbFactory.Context.KubernetesClusters.ToDictionaryAsync(c => c.Id, c => c.ActiveReporterMachineId);
        await Assert.That(active[activeCluster]).IsNull();
        await Assert.That(active[siblingCluster]).IsEqualTo(siblingMachineId);
        await Assert.That(active[foreignCluster]).IsEqualTo(doomedMachineId);
        await Assert.That((await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id == activeCluster)).LastSnapshotReceivedAt).IsNotNull();
    }

    [Test]
    public async Task SoftDelete_MachineOfAnotherTenant_LeavesCandidateAndActiveReporterAlone()
    {
        // Intent: a delete that matches no active machine of the caller's tenant changes nothing, so a
        // foreign machine id cannot be used to strip another tenant's candidate or active reporter.
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildUser());
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        int otherTenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: tenantId));
        long clusterId = await SeedReportingClusterAsync(dbFactory, tenantId, machineId);
        await SeedKubernetesCandidateAsync(dbFactory, tenantId, clusterId, machineId);

        string? deletedKeyHash = await repo.SoftDeleteMachineAsync(machineId, otherTenantId, userId, CancellationToken.None);

        await Assert.That(deletedKeyHash).IsNull();
        await Assert.That(await dbFactory.Context.KubernetesReporterCandidates.CountAsync(c => c.MachineId == machineId)).IsEqualTo(1);
        await Assert.That((await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id == clusterId)).ActiveReporterMachineId).IsEqualTo(machineId);
    }

    // ========== GetHostnameMapAsync tests ==========

    [Test]
    public async Task GetHostnameMapAsync_WithData_ReturnsMachineIdToHostnameMapping()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 1, tenantId: 1));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 2, tenantId: 1));

        Dictionary<long, string?> result = await repo.GetHostnameMapAsync(new List<long> { 1, 2 });

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.ContainsKey(1)).IsTrue();
        await Assert.That(result.ContainsKey(2)).IsTrue();
        // Default hostname from BuildMachineStateSummary is "host-{machineId}"
        await Assert.That(result[1]).IsEqualTo("host-1");
        await Assert.That(result[2]).IsEqualTo("host-2");
    }

    [Test]
    public async Task GetHostnameMapAsync_EmptyList_ReturnsEmptyDictionary()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Dictionary<long, string?> result = await repo.GetHostnameMapAsync(new List<long>());

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetHostnameMapAsync_PartialMatch_ReturnsOnlyMatchingEntries()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 10, tenantId: 1));

        // Request includes an ID that does not exist in the table
        Dictionary<long, string?> result = await repo.GetHostnameMapAsync(new List<long> { 10, 999 });

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result.ContainsKey(10)).IsTrue();
        await Assert.That(result.ContainsKey(999)).IsFalse();
    }

    // ========== GetNameMapAsync tests ==========



    // ========== GetSummaryListByMachineIdsAsync tests ==========

    [Test]
    public async Task GetSummaryListByMachineIdsAsync_WithData_ReturnsList()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 21, tenantId: 1));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 22, tenantId: 1));

        List<MachineStateSummary> result = await repo.GetSummaryListByMachineIdsAsync(new List<long> { 21, 22 });

        await Assert.That(result.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetSummaryListByMachineIdsAsync_EmptyList_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<MachineStateSummary> result = await repo.GetSummaryListByMachineIdsAsync(new List<long>());

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== UpdateSummaryNameAsync tests ==========

    [Test]
    public async Task UpdateSummaryNameAsync_ExistingRow_UpdatesName()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int _, int tenantId, long machineId) = await SeedMachineWithStateAsync(dbFactory);

        await repo.UpdateSummaryNameAsync(machineId, tenantId, "renamed-box");

        MachineStateSummary? updated = await repo.GetSummaryForMachineAsync(machineId);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Name).IsEqualTo("renamed-box");
    }

    [Test]
    public async Task UpdateSummaryNameAsync_WrongTenant_ReturnsFalse_AndNoChange()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int _, int tenantId, long machineId) = await SeedMachineWithStateAsync(dbFactory);

        MachineStateSummary? before = await repo.GetSummaryForMachineAsync(machineId);
        await Assert.That(before).IsNotNull();
        string originalName = before!.Name;

        int otherTenantId = tenantId + 1000;
        bool result = await repo.UpdateSummaryNameAsync(machineId, otherTenantId, "renamed-box");

        await Assert.That(result).IsFalse();

        MachineStateSummary? unchanged = await repo.GetSummaryForMachineAsync(machineId);
        await Assert.That(unchanged).IsNotNull();
        await Assert.That(unchanged!.Name).IsEqualTo(originalName);
    }

    [Test]
    public async Task UpdateSummaryNameAsync_CorrectTenant_ReturnsTrue_AndChanges()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int _, int tenantId, long machineId) = await SeedMachineWithStateAsync(dbFactory);

        bool result = await repo.UpdateSummaryNameAsync(machineId, tenantId, "renamed-box");

        await Assert.That(result).IsTrue();

        MachineStateSummary? updated = await repo.GetSummaryForMachineAsync(machineId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Name).IsEqualTo("renamed-box");
    }

    // ========== GetTelemetryBatchAsync tests ==========

    [Test]
    public async Task GetTelemetryBatchAsync_WithRows_ReturnsCorrectBatch()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Insert telemetry rows with explicit ReceivedAt within the streaming window
        MachineTelemetry row1 = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 1);
        row1.ReceivedAt = now.AddMinutes(-5);
        row1.ServerReceivedAt = now.AddMinutes(-5);
        await repo.InsertTelemetryAsync(row1);

        MachineTelemetry row2 = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 2);
        row2.ReceivedAt = now.AddMinutes(-3);
        row2.ServerReceivedAt = now.AddMinutes(-3);
        await repo.InsertTelemetryAsync(row2);

        MachineTelemetry row3 = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 3);
        row3.ReceivedAt = now.AddMinutes(-1);
        row3.ServerReceivedAt = now.AddMinutes(-1);
        await repo.InsertTelemetryAsync(row3);

        // Fetch batch with high water mark of 0 and a streaming window in the past
        DateTimeOffset streamingWindow = now.AddMinutes(-10);
        List<MachineTelemetry> batch = await repo.GetTelemetryBatchAsync(0, streamingWindow, DateTimeOffset.UtcNow.AddDays(1), batchSize: 10, shardIndex: 0, shardCount: 1);

        await Assert.That(batch.Count).IsEqualTo(3);
        // Verify ascending order by Id
        await Assert.That(batch[0].Id < batch[1].Id).IsTrue();
        await Assert.That(batch[1].Id < batch[2].Id).IsTrue();
    }

    [Test]
    public async Task GetTelemetryBatchAsync_RespectsHighWaterMark_OnlyReturnsNewerRows()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        MachineTelemetry row1 = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 1);
        row1.ReceivedAt = now.AddMinutes(-5);
        row1.ServerReceivedAt = now.AddMinutes(-5);
        await repo.InsertTelemetryAsync(row1);

        MachineTelemetry row2 = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 2);
        row2.ReceivedAt = now.AddMinutes(-3);
        row2.ServerReceivedAt = now.AddMinutes(-3);
        await repo.InsertTelemetryAsync(row2);

        // Get all rows to discover the first row's ID
        DateTimeOffset streamingWindow = now.AddMinutes(-10);
        List<MachineTelemetry> allRows = await repo.GetTelemetryBatchAsync(0, streamingWindow, DateTimeOffset.UtcNow.AddDays(1), batchSize: 10, shardIndex: 0, shardCount: 1);
        long firstRowId = allRows[0].Id;

        // Fetch batch using first row's ID as the high water mark
        List<MachineTelemetry> batch = await repo.GetTelemetryBatchAsync(firstRowId, streamingWindow, DateTimeOffset.UtcNow.AddDays(1), batchSize: 10, shardIndex: 0, shardCount: 1);

        await Assert.That(batch.Count).IsEqualTo(1);
        await Assert.That(batch[0].Id > firstRowId).IsTrue();
    }

    [Test]
    public async Task GetTelemetryBatchAsync_RespectsStreamingWindow_ExcludesOldRows()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Insert a row that is too old for the streaming window
        MachineTelemetry oldRow = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 1);
        oldRow.ReceivedAt = now.AddHours(-2);
        oldRow.ServerReceivedAt = now.AddHours(-2);
        await repo.InsertTelemetryAsync(oldRow);

        // Insert a row within the streaming window
        MachineTelemetry newRow = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 2);
        newRow.ReceivedAt = now.AddMinutes(-5);
        newRow.ServerReceivedAt = now.AddMinutes(-5);
        await repo.InsertTelemetryAsync(newRow);

        // Set the streaming window to 1 hour ago, so the old row is excluded
        DateTimeOffset streamingWindow = now.AddHours(-1);
        List<MachineTelemetry> batch = await repo.GetTelemetryBatchAsync(0, streamingWindow, DateTimeOffset.UtcNow.AddDays(1), batchSize: 10, shardIndex: 0, shardCount: 1);

        await Assert.That(batch.Count).IsEqualTo(1);
        await Assert.That(batch[0].TelemetryType).IsEqualTo((short)2);
    }

    [Test]
    public async Task GetTelemetryBatchAsync_WithSharding_OnlyReturnsOwnedMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset streamingWindow = now.AddMinutes(-10);

        // Machines 10 and 11 sit in different shards under modulo-2 partitioning (10 % 2 == 0, 11 % 2 == 1).
        MachineTelemetry even = TestDataBuilder.BuildMachineTelemetry(machineId: 10, tenantId: 1, telemetryType: 1);
        even.ReceivedAt = now.AddMinutes(-5);
        even.ServerReceivedAt = now.AddMinutes(-5);
        await repo.InsertTelemetryAsync(even);

        MachineTelemetry odd = TestDataBuilder.BuildMachineTelemetry(machineId: 11, tenantId: 1, telemetryType: 1);
        odd.ReceivedAt = now.AddMinutes(-4);
        odd.ServerReceivedAt = now.AddMinutes(-4);
        await repo.InsertTelemetryAsync(odd);

        List<MachineTelemetry> shardZero = await repo.GetTelemetryBatchAsync(0, streamingWindow, DateTimeOffset.UtcNow.AddDays(1), batchSize: 10, shardIndex: 0, shardCount: 2);
        List<MachineTelemetry> shardOne = await repo.GetTelemetryBatchAsync(0, streamingWindow, DateTimeOffset.UtcNow.AddDays(1), batchSize: 10, shardIndex: 1, shardCount: 2);

        await Assert.That(shardZero.Count).IsEqualTo(1);
        await Assert.That(shardZero[0].MachineId).IsEqualTo(10L);
        await Assert.That(shardOne.Count).IsEqualTo(1);
        await Assert.That(shardOne[0].MachineId).IsEqualTo(11L);
    }

    [Test]
    public async Task GetTelemetryBatchAsync_ExcludesRowsNewerThanVisibilityCutoff()
    {
        // A row whose server receipt is within the safety-lag window (ServerReceivedAt > cutoff) is held
        // back even when Id > hwm; rows at or before the cutoff are projected. This gives out-of-order
        // commits time to become visible before the cursor passes them.
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset streamingWindow = now.AddDays(-1);
        DateTimeOffset cutoff = now.AddSeconds(-5); // 5-second visibility lag

        // All rows carry a recent (in-window) ReceivedAt; they differ only in ServerReceivedAt.
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 1, receivedAt: now, id: 1, serverReceivedAt: now);                 // too new — excluded
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 1, receivedAt: now, id: 2, serverReceivedAt: now.AddSeconds(-6));  // older than lag — included
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 1, receivedAt: now, id: 3, serverReceivedAt: cutoff);              // exactly at cutoff — included (<=)

        List<MachineTelemetry> batch = await repo.GetTelemetryBatchAsync(0, streamingWindow, cutoff, batchSize: 10, shardIndex: 0, shardCount: 1);

        List<long> ids = batch.Select(r => r.Id).OrderBy(id => id).ToList();
        await Assert.That(ids).IsEquivalentTo(new List<long> { 2L, 3L });
    }

    [Test]
    public async Task GetTelemetryBatchAsync_SingleShard_ReturnsAllMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset streamingWindow = now.AddMinutes(-10);

        MachineTelemetry even = TestDataBuilder.BuildMachineTelemetry(machineId: 10, tenantId: 1, telemetryType: 1);
        even.ReceivedAt = now.AddMinutes(-5);
        even.ServerReceivedAt = now.AddMinutes(-5);
        await repo.InsertTelemetryAsync(even);

        MachineTelemetry odd = TestDataBuilder.BuildMachineTelemetry(machineId: 11, tenantId: 1, telemetryType: 1);
        odd.ReceivedAt = now.AddMinutes(-4);
        odd.ServerReceivedAt = now.AddMinutes(-4);
        await repo.InsertTelemetryAsync(odd);

        // shardCount of 1 disables the modulo predicate, so both machines are returned.
        List<MachineTelemetry> batch = await repo.GetTelemetryBatchAsync(0, streamingWindow, DateTimeOffset.UtcNow.AddDays(1), batchSize: 10, shardIndex: 0, shardCount: 1);

        await Assert.That(batch.Count).IsEqualTo(2);
    }

    // ========== GetLatestTelemetryPerTypeAsync tests ==========

    [Test]
    public async Task GetLatestTelemetryPerTypeAsync_MultipleTypes_ReturnsLatestPerType()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Two rows for type 1 - only the latest should be returned
        MachineTelemetry type1Old = TestDataBuilder.BuildMachineTelemetry(machineId: 50, tenantId: 1, telemetryType: 1, payload: """{"old":true}""");
        type1Old.ReceivedAt = now.AddMinutes(-30);
        type1Old.ServerReceivedAt = now.AddMinutes(-30);
        await repo.InsertTelemetryAsync(type1Old);

        MachineTelemetry type1New = TestDataBuilder.BuildMachineTelemetry(machineId: 50, tenantId: 1, telemetryType: 1, payload: """{"old":false}""");
        type1New.ReceivedAt = now.AddMinutes(-5);
        type1New.ServerReceivedAt = now.AddMinutes(-5);
        await repo.InsertTelemetryAsync(type1New);

        // One row for type 2
        MachineTelemetry type2 = TestDataBuilder.BuildMachineTelemetry(machineId: 50, tenantId: 1, telemetryType: 2, payload: """{"type":2}""");
        type2.ReceivedAt = now.AddMinutes(-10);
        type2.ServerReceivedAt = now.AddMinutes(-10);
        await repo.InsertTelemetryAsync(type2);

        Dictionary<short, MachineTelemetry> result = await repo.GetLatestTelemetryPerTypeAsync(50, daysBack: 1);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.ContainsKey(1)).IsTrue();
        await Assert.That(result.ContainsKey(2)).IsTrue();
        await Assert.That(result[1].Payload).IsEqualTo("""{"old":false}""");
        await Assert.That(result[2].Payload).IsEqualTo("""{"type":2}""");
    }

    [Test]
    public async Task GetLatestTelemetryPerTypeAsync_OldDataBeyondDaysBack_ExcludesOldRows()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Row received 10 days ago
        MachineTelemetry oldRow = TestDataBuilder.BuildMachineTelemetry(machineId: 60, tenantId: 1, telemetryType: 1);
        oldRow.ReceivedAt = now.AddDays(-10);
        oldRow.ServerReceivedAt = now.AddDays(-10);
        await repo.InsertTelemetryAsync(oldRow);

        // Row received 1 hour ago
        MachineTelemetry recentRow = TestDataBuilder.BuildMachineTelemetry(machineId: 60, tenantId: 1, telemetryType: 2);
        recentRow.ReceivedAt = now.AddHours(-1);
        recentRow.ServerReceivedAt = now.AddHours(-1);
        await repo.InsertTelemetryAsync(recentRow);

        // Only look back 3 days - the 10-day-old row should be excluded
        Dictionary<short, MachineTelemetry> result = await repo.GetLatestTelemetryPerTypeAsync(60, daysBack: 3);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result.ContainsKey(2)).IsTrue();
    }

    // ========== GetRecentTelemetryAsync tests ==========

    [Test]
    public async Task GetRecentTelemetryAsync_MultipleRows_ReturnsInDescendingOrder()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        MachineTelemetry row1 = TestDataBuilder.BuildMachineTelemetry(machineId: 70, tenantId: 1, telemetryType: 5, payload: """{"order":1}""");
        row1.ReceivedAt = now.AddMinutes(-30);
        row1.ServerReceivedAt = now.AddMinutes(-30);
        await repo.InsertTelemetryAsync(row1);

        MachineTelemetry row2 = TestDataBuilder.BuildMachineTelemetry(machineId: 70, tenantId: 1, telemetryType: 5, payload: """{"order":2}""");
        row2.ReceivedAt = now.AddMinutes(-20);
        row2.ServerReceivedAt = now.AddMinutes(-20);
        await repo.InsertTelemetryAsync(row2);

        MachineTelemetry row3 = TestDataBuilder.BuildMachineTelemetry(machineId: 70, tenantId: 1, telemetryType: 5, payload: """{"order":3}""");
        row3.ReceivedAt = now.AddMinutes(-10);
        row3.ServerReceivedAt = now.AddMinutes(-10);
        await repo.InsertTelemetryAsync(row3);

        List<MachineTelemetry> result = await repo.GetRecentTelemetryAsync(70, telemetryType: 5, limit: 10);

        await Assert.That(result.Count).IsEqualTo(3);
        // Most recent first (descending by ReceivedAt)
        await Assert.That(result[0].Payload).IsEqualTo("""{"order":3}""");
        await Assert.That(result[2].Payload).IsEqualTo("""{"order":1}""");
    }

    [Test]
    public async Task GetRecentTelemetryAsync_RespectsLimit_ReturnsOnlyRequestedCount()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 5; i++)
        {
            MachineTelemetry row = TestDataBuilder.BuildMachineTelemetry(machineId: 80, tenantId: 1, telemetryType: 7);
            row.ReceivedAt = now.AddMinutes(-i);
            row.ServerReceivedAt = now.AddMinutes(-i);
            await repo.InsertTelemetryAsync(row);
        }

        List<MachineTelemetry> result = await repo.GetRecentTelemetryAsync(80, telemetryType: 7, limit: 2);

        await Assert.That(result.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetRecentTelemetryAsync_DifferentType_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        MachineTelemetry row = TestDataBuilder.BuildMachineTelemetry(machineId: 90, tenantId: 1, telemetryType: 1);
        await repo.InsertTelemetryAsync(row);

        // Query for a different telemetry type than what was inserted
        List<MachineTelemetry> result = await repo.GetRecentTelemetryAsync(90, telemetryType: 99, limit: 10);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== GetTelemetryExportBatchAsync tests ==========

    [Test]
    public async Task GetTelemetryExportBatchAsync_CursorPaginationAndBatchSize_ReturnsCorrectBatch()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Insert 5 telemetry rows for machine 1
        for (int i = 0; i < 5; i++)
        {
            MachineTelemetry row = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 1);
            row.ReceivedAt = now.AddMinutes(-i);
            row.ServerReceivedAt = now.AddMinutes(-i);
            await repo.InsertTelemetryAsync(row);
        }

        // Get first batch of 2 starting from id 0
        List<MachineTelemetry> batch1 = await repo.GetTelemetryExportBatchAsync(new List<long> { 1 }, 0, 2);

        await Assert.That(batch1.Count).IsEqualTo(2);
        // Verify ascending order by Id
        await Assert.That(batch1[0].Id < batch1[1].Id).IsTrue();

        // Get next batch using the last id from batch1 as cursor
        long cursor = batch1[1].Id;
        List<MachineTelemetry> batch2 = await repo.GetTelemetryExportBatchAsync(new List<long> { 1 }, cursor, 2);

        await Assert.That(batch2.Count).IsEqualTo(2);
        // All IDs in batch2 should be greater than the cursor
        await Assert.That(batch2[0].Id > cursor).IsTrue();
        await Assert.That(batch2[0].Id < batch2[1].Id).IsTrue();

        // Get final batch - should have only 1 remaining row
        long cursor2 = batch2[1].Id;
        List<MachineTelemetry> batch3 = await repo.GetTelemetryExportBatchAsync(new List<long> { 1 }, cursor2, 2);

        await Assert.That(batch3.Count).IsEqualTo(1);
        await Assert.That(batch3[0].Id > cursor2).IsTrue();
    }

    [Test]
    // Interior/healthy baseline.
    [Arguments(10, 10, 10, 0, false, false, null, (short)0)]
    // CPU: below warning, at warning, below critical, at critical.
    [Arguments(79, 10, 10, 0, false, false, null, (short)0)]
    [Arguments(80, 10, 10, 0, false, false, null, (short)1)]
    [Arguments(94, 10, 10, 0, false, false, null, (short)1)]
    [Arguments(95, 10, 10, 0, false, false, null, (short)2)]
    // Memory, same four boundaries.
    [Arguments(10, 79, 10, 0, false, false, null, (short)0)]
    [Arguments(10, 80, 10, 0, false, false, null, (short)1)]
    [Arguments(10, 94, 10, 0, false, false, null, (short)1)]
    [Arguments(10, 95, 10, 0, false, false, null, (short)2)]
    // Max disk usage, same four boundaries.
    [Arguments(10, 10, 79, 0, false, false, null, (short)0)]
    [Arguments(10, 10, 80, 0, false, false, null, (short)1)]
    [Arguments(10, 10, 94, 0, false, false, null, (short)1)]
    [Arguments(10, 10, 95, 0, false, false, null, (short)2)]
    // Failed services and each hardware flag, in isolation.
    [Arguments(10, 10, 10, 1, false, false, null, (short)2)]
    [Arguments(10, 10, 10, 0, true, false, null, (short)2)]
    [Arguments(10, 10, 10, 0, false, true, null, (short)2)]
    // SSD wear, which the sweep raises to Warning and never to Critical: the value is monotonic,
    // so a Critical clause would pin the machine there permanently and hide a real transient one.
    [Arguments(10, 10, 10, 0, false, false, null, (short)0)]
    [Arguments(10, 10, 10, 0, false, false, 79, (short)0)]
    [Arguments(10, 10, 10, 0, false, false, 80, (short)1)]
    [Arguments(10, 10, 10, 0, false, false, 99, (short)1)]
    // A worn disk must not mask a critical metric on the same machine.
    [Arguments(95, 10, 10, 0, false, false, 99, (short)2)]
    public async Task SweepHealthStatusAsync_AtEveryThresholdBoundary_WritesTheExpectedStatus(
        int cpuPercent, int memoryPercent, int maxDiskUsagePercent, int failedServices,
        bool hasDiskHealthIssue, bool hasHardwareIssue, int? maxDiskWearoutPercent,
        short expectedHealthStatus)
    {
        // The sweep SQL is now the only place the health thresholds are written down, so the
        // thresholds themselves are pinned by running that SQL rather than by a second copy of
        // the rule in C#. Each case sits directly at or just below a boundary, where an off-by-one
        // in the SQL shows up and an interior data point would not.
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(
            machineId: machineId,
            tenantId: tenantId,
            cpuPercent: cpuPercent,
            memoryPercent: memoryPercent,
            healthStatus: 0,
            lastSeenAt: DateTimeOffset.UtcNow);
        summary.FailedServices = failedServices;
        summary.MaxDiskUsagePercent = maxDiskUsagePercent;
        summary.HasDiskHealthIssue = hasDiskHealthIssue;
        summary.HasHardwareIssue = hasHardwareIssue;
        summary.MaxDiskWearoutPercent = maxDiskWearoutPercent;
        await dbFactory.Context.InsertAsync(summary);

        SqliteSqlDialect dialect = new();
        await repo.SweepHealthStatusAsync(dialect.HealthSweepForTenant, tenantId, 300, 300);

        MachineStateSummary? updated = await repo.GetSummaryForMachineAsync(machineId);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.HealthStatus).IsEqualTo(expectedHealthStatus);
    }

    [Test]
    public async Task SweepHealthStatusAsync_StaleLastSeen_WritesOfflineOverAnyMetricVerdict()
    {
        // Offline outranks every metric branch: a machine the server has not heard from is
        // reported Offline even though its last known metrics were critical.
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(
            machineId: machineId,
            tenantId: tenantId,
            cpuPercent: 99,
            memoryPercent: 99,
            healthStatus: 0,
            lastSeenAt: DateTimeOffset.UtcNow.AddHours(-1));
        summary.FailedServices = 5;
        await dbFactory.Context.InsertAsync(summary);

        SqliteSqlDialect dialect = new();
        await repo.SweepHealthStatusAsync(dialect.HealthSweepForTenant, tenantId, 300, 300);

        MachineStateSummary? updated = await repo.GetSummaryForMachineAsync(machineId);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.HealthStatus).IsEqualTo((short)3);
    }

    // ========== GetFleetHealthAggregationAsync tests ==========

    [Test]
    public async Task GetFleetHealthAggregationAsync_MultipleMachines_ReturnsHealthCountsAndSecurityUpdatesSum()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Machine 1: healthy (health=0) with 5 security updates
        Machine m1 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m1Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);
        MachineStateSummary s1 = TestDataBuilder.BuildMachineStateSummary(machineId: m1Id, tenantId: tenantId, healthStatus: 0);
        s1.SecurityUpdates = 5;
        await dbFactory.Context.InsertAsync(s1);

        // Machine 2: warning (health=1) with 3 security updates
        Machine m2 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m2Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m2);
        MachineStateSummary s2 = TestDataBuilder.BuildMachineStateSummary(machineId: m2Id, tenantId: tenantId, healthStatus: 1);
        s2.SecurityUpdates = 3;
        await dbFactory.Context.InsertAsync(s2);

        // Machine 3: critical (health=2) with 7 security updates
        Machine m3 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m3Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m3);
        MachineStateSummary s3 = TestDataBuilder.BuildMachineStateSummary(machineId: m3Id, tenantId: tenantId, healthStatus: 2);
        s3.SecurityUpdates = 7;
        await dbFactory.Context.InsertAsync(s3);

        (List<(short HealthStatus, int Count)> healthCounts, int totalSecurityUpdates, int pausedCount) =
            await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.None, 0);

        await Assert.That(healthCounts.Count).IsEqualTo(3);
        await Assert.That(totalSecurityUpdates).IsEqualTo(15);
        await Assert.That(pausedCount).IsEqualTo(0);

        // Verify each health status has exactly 1 machine
        (short HealthStatus, int Count) healthyGroup = healthCounts.First(h => h.HealthStatus == 0);
        await Assert.That(healthyGroup.Count).IsEqualTo(1);
        (short HealthStatus, int Count) warningGroup = healthCounts.First(h => h.HealthStatus == 1);
        await Assert.That(warningGroup.Count).IsEqualTo(1);
        (short HealthStatus, int Count) criticalGroup = healthCounts.First(h => h.HealthStatus == 2);
        await Assert.That(criticalGroup.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetFleetHealthAggregationAsync_EmptyTenant_ReturnsEmptyHealthCountsAndZeroSecurityUpdates()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        (List<(short HealthStatus, int Count)> healthCounts, int totalSecurityUpdates, int pausedCount) =
            await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.None, 0);

        await Assert.That(healthCounts.Count).IsEqualTo(0);
        await Assert.That(totalSecurityUpdates).IsEqualTo(0);
        await Assert.That(pausedCount).IsEqualTo(0);
    }

    // ========== SearchFleetMachinesAsync paging/sort tests (ported from the removed fleet-page method) ==========

    [Test]
    public async Task SearchFleetMachinesAsync_BasicPagination_ReturnsTotalCountAndRequestedPage()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Seed 5 machines with summary rows
        for (int i = 0; i < 5; i++)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
            long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
            await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: tenantId));
        }

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { SortBy = "name", Skip = 0, Take = 2 });

        await Assert.That(totalCount).IsEqualTo(5);
        await Assert.That(rows.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SearchFleetMachinesAsync_SingleHealthStatusValue_ReturnsOnlyMatchingHealth()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Healthy machine
        Machine healthy = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long healthyId = await dbFactory.Context.InsertWithInt64IdentityAsync(healthy);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: healthyId, tenantId: tenantId, healthStatus: 0));

        // Warning machine
        Machine warning = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long warningId = await dbFactory.Context.InsertWithInt64IdentityAsync(warning);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: warningId, tenantId: tenantId, healthStatus: 1));

        // Critical machine
        Machine critical = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long criticalId = await dbFactory.Context.InsertWithInt64IdentityAsync(critical);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: criticalId, tenantId: tenantId, healthStatus: 2));

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { HealthStatusValues = [0], SortBy = "name", Skip = 0, Take = 10 });

        await Assert.That(totalCount).IsEqualTo(1);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].HealthStatus).IsEqualTo((short)0);
    }

    [Test]
    public async Task SearchFleetMachinesAsync_TextSearch_ReturnsMatchingMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Machine with a searchable name
        Machine webServer = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: "web-prod-01");
        long webId = await dbFactory.Context.InsertWithInt64IdentityAsync(webServer);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: webId, tenantId: tenantId, name: "web-prod-01"));

        // Machine with a different name
        Machine dbServer = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: "db-prod-01");
        long dbId = await dbFactory.Context.InsertWithInt64IdentityAsync(dbServer);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: dbId, tenantId: tenantId, name: "db-prod-01"));

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { Search = "web", SortBy = "name", Skip = 0, Take = 10 });

        await Assert.That(totalCount).IsEqualTo(1);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Name).IsEqualTo("web-prod-01");
    }

    [Test]
    public async Task SearchFleetMachinesAsync_SortByCpuDescending_ReturnsMachinesInDescendingCpuOrder()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Machine with low CPU
        Machine lowCpu = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long lowId = await dbFactory.Context.InsertWithInt64IdentityAsync(lowCpu);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: lowId, tenantId: tenantId, cpuPercent: 10));

        // Machine with high CPU
        Machine highCpu = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long highId = await dbFactory.Context.InsertWithInt64IdentityAsync(highCpu);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: highId, tenantId: tenantId, cpuPercent: 90));

        // Machine with mid CPU
        Machine midCpu = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long midId = await dbFactory.Context.InsertWithInt64IdentityAsync(midCpu);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: midId, tenantId: tenantId, cpuPercent: 50));

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { SortBy = "cpu", SortDescending = true, Skip = 0, Take = 10 });

        await Assert.That(totalCount).IsEqualTo(3);
        await Assert.That(rows.Count).IsEqualTo(3);
        // Verify descending order: highest CPU first
        await Assert.That(rows[0].CpuUsagePercent).IsEqualTo(90);
        await Assert.That(rows[1].CpuUsagePercent).IsEqualTo(50);
        await Assert.That(rows[2].CpuUsagePercent).IsEqualTo(10);
    }

    // ========== SearchFleetMachinesAsync tests ==========

    [Test]
    public async Task SearchFleetMachinesAsync_CpuRangeFilter_ReturnsOnlyMachinesWithinRange()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Machine with CPU 20
        Machine low = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long lowId = await dbFactory.Context.InsertWithInt64IdentityAsync(low);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: lowId, tenantId: tenantId, cpuPercent: 20));

        // Machine with CPU 50
        Machine mid = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long midId = await dbFactory.Context.InsertWithInt64IdentityAsync(mid);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: midId, tenantId: tenantId, cpuPercent: 50));

        // Machine with CPU 80
        Machine high = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long highId = await dbFactory.Context.InsertWithInt64IdentityAsync(high);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: highId, tenantId: tenantId, cpuPercent: 80));

        FleetSearchParameters parameters = new()
        {
            CpuMin = 40,
            CpuMax = 60,
            Skip = 0,
            Take = 10,
        };

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(tenantId, parameters);

        await Assert.That(totalCount).IsEqualTo(1);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].CpuUsagePercent).IsEqualTo(50);
    }

    [Test]
    public async Task SearchFleetMachinesAsync_HealthStatusFilter_ReturnsOnlyMatchingStatuses()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Seed machines with health 0, 1, 2, 3
        short[] statuses = { 0, 1, 2, 3 };
        foreach (short status in statuses)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
            long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
            await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: tenantId, healthStatus: status));
        }

        FleetSearchParameters parameters = new()
        {
            HealthStatusValues = new List<short> { 1, 2 },
            Skip = 0,
            Take = 10,
        };

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(tenantId, parameters);

        await Assert.That(totalCount).IsEqualTo(2);
        await Assert.That(rows.Count).IsEqualTo(2);
        // All returned rows should have health status 1 or 2
        await Assert.That(rows.All(r => (r.HealthStatus == 1) || (r.HealthStatus == 2))).IsTrue();
    }

    [Test]
    public async Task SearchFleetMachinesAsync_CombinedSearchAndHealthFilter_ReturnsMatchingResults()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // web-alpha, healthy
        Machine webAlpha = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: "web-alpha");
        long webAlphaId = await dbFactory.Context.InsertWithInt64IdentityAsync(webAlpha);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: webAlphaId, tenantId: tenantId, name: "web-alpha", healthStatus: 0));

        // web-beta, warning
        Machine webBeta = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: "web-beta");
        long webBetaId = await dbFactory.Context.InsertWithInt64IdentityAsync(webBeta);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: webBetaId, tenantId: tenantId, name: "web-beta", healthStatus: 1));

        // db-gamma, warning
        Machine dbGamma = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: "db-gamma");
        long dbGammaId = await dbFactory.Context.InsertWithInt64IdentityAsync(dbGamma);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: dbGammaId, tenantId: tenantId, name: "db-gamma", healthStatus: 1));

        // Search for "web" with health status = warning (1)
        FleetSearchParameters parameters = new()
        {
            Search = "web",
            HealthStatusValues = new List<short> { 1 },
            Skip = 0,
            Take = 10,
        };

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(tenantId, parameters);

        // Only web-beta should match (name contains "web" AND health status is 1)
        await Assert.That(totalCount).IsEqualTo(1);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Name).IsEqualTo("web-beta");
    }

    // ========== SweepHealthStatusAsync tests ==========

    [Test]
    public async Task SweepHealthStatusAsync_WithKnownMetrics_UpdatesHealthStatusCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        DateTimeOffset recentTime = DateTimeOffset.UtcNow;

        // Machine 1: healthy - low CPU, low memory, no failed services, recently seen
        Machine m1 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m1Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);
        MachineStateSummary s1 = TestDataBuilder.BuildMachineStateSummary(machineId: m1Id, tenantId: tenantId, cpuPercent: 30, memoryPercent: 40, healthStatus: 0, lastSeenAt: recentTime);
        s1.FailedServices = 0;
        await dbFactory.Context.InsertAsync(s1);

        // Machine 2: should become critical - high CPU (95%+)
        Machine m2 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m2Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m2);
        MachineStateSummary s2 = TestDataBuilder.BuildMachineStateSummary(machineId: m2Id, tenantId: tenantId, cpuPercent: 96, memoryPercent: 40, healthStatus: 0, lastSeenAt: recentTime);
        s2.FailedServices = 0;
        await dbFactory.Context.InsertAsync(s2);

        // Machine 3: should become warning - CPU 80-94
        Machine m3 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m3Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m3);
        MachineStateSummary s3 = TestDataBuilder.BuildMachineStateSummary(machineId: m3Id, tenantId: tenantId, cpuPercent: 85, memoryPercent: 40, healthStatus: 0, lastSeenAt: recentTime);
        s3.FailedServices = 0;
        await dbFactory.Context.InsertAsync(s3);

        // Machine 4: should become critical - failed services > 0
        Machine m4 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m4Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m4);
        MachineStateSummary s4 = TestDataBuilder.BuildMachineStateSummary(machineId: m4Id, tenantId: tenantId, cpuPercent: 10, memoryPercent: 10, healthStatus: 0, lastSeenAt: recentTime);
        s4.FailedServices = 2;
        await dbFactory.Context.InsertAsync(s4);

        SqliteSqlDialect dialect = new();
        int rowsAffected = await repo.SweepHealthStatusAsync(dialect.HealthSweepForTenant, tenantId, 300, 300);

        // At least 3 machines should have had their status changed (m2, m3, m4 all started at 0)
        await Assert.That(rowsAffected >= 3).IsTrue();

        // Verify individual machine health statuses
        MachineStateSummary? updated1 = await repo.GetSummaryForMachineAsync(m1Id);
        await Assert.That(updated1).IsNotNull();
        await Assert.That(updated1!.HealthStatus).IsEqualTo((short)0);

        MachineStateSummary? updated2 = await repo.GetSummaryForMachineAsync(m2Id);
        await Assert.That(updated2).IsNotNull();
        await Assert.That(updated2!.HealthStatus).IsEqualTo((short)2);

        MachineStateSummary? updated3 = await repo.GetSummaryForMachineAsync(m3Id);
        await Assert.That(updated3).IsNotNull();
        await Assert.That(updated3!.HealthStatus).IsEqualTo((short)1);

        MachineStateSummary? updated4 = await repo.GetSummaryForMachineAsync(m4Id);
        await Assert.That(updated4).IsNotNull();
        await Assert.That(updated4!.HealthStatus).IsEqualTo((short)2);
    }

    // ========== GetFleetHealthAggregationAsync tests ==========

    [Test]
    public async Task GetFleetHealthAggregationAsync_GroupsByHealthStatusAndSumsSecurityUpdates()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Two healthy (status 0) and one critical (status 2). Security updates sum to 12.
        await SeedFleetMachineAsync(dbFactory, tenantId, healthStatus: 0, securityUpdates: 2);
        await SeedFleetMachineAsync(dbFactory, tenantId, healthStatus: 0, securityUpdates: 3);
        await SeedFleetMachineAsync(dbFactory, tenantId, healthStatus: 2, securityUpdates: 7);

        (List<(short HealthStatus, int Count)> healthCounts, int totalSecurityUpdates, int pausedCount) =
            await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.None, 0, CancellationToken.None);

        Dictionary<short, int> byStatus = healthCounts.ToDictionary(x => x.HealthStatus, x => x.Count);
        await Assert.That(byStatus[0]).IsEqualTo(2);
        await Assert.That(byStatus[2]).IsEqualTo(1);
        await Assert.That(totalSecurityUpdates).IsEqualTo(12);
        await Assert.That(pausedCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetFleetHealthAggregationAsync_AfterCutoff_CountsPausedMachinesOutsideEveryHealthBucket()
    {
        // The two machines past the cutoff last reported warning and offline. Neither may land in
        // those buckets, because the health they froze at is not a statement about the machine now.
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        List<long> ids = await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [0, 0, 2, 1, 3]);

        (List<(short HealthStatus, int Count)> healthCounts, _, int pausedCount) =
            await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.After, ids[2], CancellationToken.None);

        Dictionary<short, int> byStatus = healthCounts.ToDictionary(x => x.HealthStatus, x => x.Count);
        await Assert.That(pausedCount).IsEqualTo(2);
        await Assert.That(byStatus.Keys.Order().ToArray()).IsEquivalentTo(new short[] { 0, 2 });
        await Assert.That(byStatus[0]).IsEqualTo(2);
        await Assert.That(byStatus[2]).IsEqualTo(1);
    }

    [Test]
    public async Task GetFleetHealthAggregationAsync_AfterCutoff_LeavesPausedMachinesOutOfTheSecurityUpdateTotal()
    {
        // Updates a paused machine last reported are not something the tenant can act on, so the
        // fleet total counts only the machines that are being watched.
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        long first = await SeedFleetMachineAsync(dbFactory, tenantId, healthStatus: 0, securityUpdates: 2);
        await SeedFleetMachineAsync(dbFactory, tenantId, healthStatus: 1, securityUpdates: 3);
        await SeedFleetMachineAsync(dbFactory, tenantId, healthStatus: 2, securityUpdates: 40);

        (_, int total, _) = await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.After, first, CancellationToken.None);
        (_, int noneTotal, _) = await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.None, 0, CancellationToken.None);
        (_, int allTotal, _) = await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.All, 0, CancellationToken.None);

        await Assert.That(total).IsEqualTo(2);
        await Assert.That(noneTotal).IsEqualTo(45);
        await Assert.That(allTotal).IsEqualTo(0);
    }

    [Test]
    public async Task GetFleetHealthAggregationAsync_AllPaused_LeavesNoHealthBucketAtAll()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [0, 2, 3]);

        (List<(short HealthStatus, int Count)> healthCounts, _, int pausedCount) =
            await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.All, 0, CancellationToken.None);

        await Assert.That(healthCounts.Count).IsEqualTo(0);
        await Assert.That(pausedCount).IsEqualTo(3);
    }

    [Test]
    public async Task GetFleetHealthAggregationAsync_AfterCutoff_IgnoresAnotherTenantsMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        int otherTenantId = await SeedTenantAsync(dbFactory);
        List<long> ids = await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [0, 0]);
        await SeedFleetMachinesAsync(dbFactory, otherTenantId, healthStatuses: [0, 0, 0]);

        (_, _, int pausedCount) = await repo.GetFleetHealthAggregationAsync(tenantId, QuotaCutoffKind.After, ids[0], CancellationToken.None);

        await Assert.That(pausedCount).IsEqualTo(1);
    }

    // ========== SearchFleetMachinesAsync paused tests ==========

    [Test]
    public async Task SearchFleetMachinesAsync_AfterCutoff_FlagsRowsPastTheCutoffAsPausedAndKeepsTheirHealth()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        List<long> ids = await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [0, 2, 1, 3]);

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[1], SortBy = "name", Skip = 0, Take = 10 });

        Dictionary<long, FleetMachineRow> byId = rows.ToDictionary(r => r.Id);
        await Assert.That(totalCount).IsEqualTo(4);
        await Assert.That(byId[ids[0]].IsPaused).IsFalse();
        await Assert.That(byId[ids[1]].IsPaused).IsFalse();
        await Assert.That(byId[ids[2]].IsPaused).IsTrue();
        await Assert.That(byId[ids[3]].IsPaused).IsTrue();

        // The row still carries the stored health; only the caller decides it is overridden.
        await Assert.That(byId[ids[3]].HealthStatus).IsEqualTo((short)3);
    }

    [Test]
    public async Task SearchFleetMachinesAsync_AllPaused_FlagsEveryRowAsPaused()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [0, 2]);

        (List<FleetMachineRow> rows, _) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { AllPaused = true, Skip = 0, Take = 10 });

        await Assert.That(rows.All(r => r.IsPaused)).IsTrue();
        await Assert.That(rows.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SearchFleetMachinesAsync_NoCutoff_FlagsNothingAsPaused()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [0, 3]);

        (List<FleetMachineRow> rows, _) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { Skip = 0, Take = 10 });

        await Assert.That(rows.Any(r => r.IsPaused)).IsFalse();
    }

    [Test]
    public async Task SearchFleetMachinesAsync_HealthFilter_NeverMatchesAPausedMachine()
    {
        // Offline is the stored health of the last machine, but it is paused, so a health filter for
        // offline must find nothing rather than a machine that is not being watched.
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        List<long> ids = await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [0, 1, 3]);

        (List<FleetMachineRow> offline, int offlineTotal) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[0], HealthStatusValues = [3], Skip = 0, Take = 10 });
        (List<FleetMachineRow> active, int activeTotal) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[0], HealthStatusValues = [0, 1, 2, 3], Skip = 0, Take = 10 });

        await Assert.That(offline.Count).IsEqualTo(0);
        await Assert.That(offlineTotal).IsEqualTo(0);
        await Assert.That(active.Select(r => r.Id).ToList()).IsEquivalentTo(new List<long> { ids[0] });
        await Assert.That(activeTotal).IsEqualTo(1);
    }

    [Test]
    public async Task SearchFleetMachinesAsync_PausedTrue_ReturnsOnlyPausedMachinesAndCountsOnlyThem()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        List<long> ids = await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [0, 2, 1, 3]);

        // Paging happens in SQL, so the paused predicate must be part of the counted and paged query.
        (List<FleetMachineRow> page, int totalCount) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[1], Paused = true, SortBy = "name", Skip = 1, Take = 1 });

        await Assert.That(totalCount).IsEqualTo(2);
        await Assert.That(page.Count).IsEqualTo(1);
        await Assert.That(page[0].IsPaused).IsTrue();
    }

    [Test]
    public async Task SearchFleetMachinesAsync_PausedFalse_ReturnsOnlyActiveMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        List<long> ids = await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [0, 2, 1, 3]);

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[1], Paused = false, Skip = 0, Take = 10 });

        await Assert.That(rows.Select(r => r.Id).Order().ToList()).IsEquivalentTo(new List<long> { ids[0], ids[1] });
        await Assert.That(totalCount).IsEqualTo(2);
    }

    [Test]
    public async Task SearchFleetMachinesAsync_SortByStatus_PutsPausedMachinesAfterEveryHealthStatus()
    {
        // The paused machines froze at healthy, warning and healthy. Sorted by their stored health they
        // would be interleaved with, or ahead of, machines that are actually critical and offline.
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        List<long> ids = await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [2, 3, 0, 1, 0]);

        (List<FleetMachineRow> ascending, _) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[1], SortBy = "status", Skip = 0, Take = 10 });
        (List<FleetMachineRow> descending, _) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[1], SortBy = "status", SortDescending = true, Skip = 0, Take = 10 });

        await Assert.That(ascending.Count).IsEqualTo(5);
        await Assert.That(string.Join(",", ascending.Take(2).Select(r => r.Id))).IsEqualTo($"{ids[0]},{ids[1]}");
        await Assert.That(ascending.Skip(2).All(r => r.IsPaused)).IsTrue();

        // Reversed, the health order inside the active machines flips but the paused bucket stays last.
        await Assert.That(string.Join(",", descending.Take(2).Select(r => r.Id))).IsEqualTo($"{ids[1]},{ids[0]}");
        await Assert.That(descending.Skip(2).All(r => r.IsPaused)).IsTrue();
    }

    [Test]
    public async Task SearchFleetMachinesAsync_SortByStatusWithNothingPaused_StillOrdersByStoredHealth()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [3, 1, 0, 2]);

        (List<FleetMachineRow> rows, _) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { SortBy = "status", Skip = 0, Take = 10 });

        await Assert.That(string.Join(",", rows.Select(r => r.HealthStatus))).IsEqualTo("0,1,2,3");
    }

    [Test]
    public async Task SearchFleetMachinesAsync_PausedWithoutACutoff_MatchesNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [0, 3]);

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(
            tenantId, new FleetSearchParameters { Paused = true, Skip = 0, Take = 10 });

        await Assert.That(rows.Count).IsEqualTo(0);
        await Assert.That(totalCount).IsEqualTo(0);
    }

    [Test]
    public async Task SearchFleetMachineIdsAsync_PausedTrue_SelectsExactlyThePausedMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        int tenantId = await SeedTenantAsync(dbFactory);
        List<long> ids = await SeedFleetMachinesAsync(dbFactory, tenantId, healthStatuses: [0, 2, 1, 3]);

        (List<long> selected, int totalCount) = await repo.SearchFleetMachineIdsAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[1], Paused = true }, 1000);
        (List<long> offline, int offlineTotal) = await repo.SearchFleetMachineIdsAsync(
            tenantId, new FleetSearchParameters { PausedAfterId = ids[1], HealthStatusValues = [3] }, 1000);

        await Assert.That(selected).IsEquivalentTo(new List<long> { ids[2], ids[3] });
        await Assert.That(totalCount).IsEqualTo(2);
        await Assert.That(offline.Count).IsEqualTo(0);
        await Assert.That(offlineTotal).IsEqualTo(0);
    }

    [Test]
    public async Task GetFleetHealthAggregationAsync_NoMachines_ReturnsEmptyAndZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (List<(short HealthStatus, int Count)> healthCounts, int totalSecurityUpdates, int pausedCount) =
            await repo.GetFleetHealthAggregationAsync(99999, QuotaCutoffKind.None, 0, CancellationToken.None);

        await Assert.That(healthCounts.Count).IsEqualTo(0);
        await Assert.That(totalSecurityUpdates).IsEqualTo(0);
        await Assert.That(pausedCount).IsEqualTo(0);
    }

    // ========== GetTelemetryPageByMachineIdsAndTypeAsync / CountTelemetryByMachineIdsAndTypeAsync ==========

    [Test]
    public async Task GetTelemetryPageByMachineIdsAndType_AppliesTimeWindowOrderingAndPaging()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset since = now.AddDays(-2);

        // Three in-window rows for machine 1 (newest first: r3, r2, r1) and one out-of-window row.
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now.AddHours(-1), id: 1);
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now.AddMinutes(-30), id: 2);
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now.AddMinutes(-5), id: 3);
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now.AddDays(-10), id: 4);
        // A different telemetry type that must be excluded.
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 1, receivedAt: now, id: 5);

        int count = await repo.CountTelemetryByMachineIdsAndTypeAsync([1], 9, since, CancellationToken.None);
        await Assert.That(count).IsEqualTo(3);

        // First page of size 2: newest two rows (id 3 then id 2).
        List<MachineTelemetry> page1 = await repo.GetTelemetryPageByMachineIdsAndTypeAsync([1], 9, since, 0, 2, CancellationToken.None);
        await Assert.That(page1.Count).IsEqualTo(2);
        await Assert.That(page1[0].Id).IsEqualTo(3L);
        await Assert.That(page1[1].Id).IsEqualTo(2L);

        // Second page of size 2: the remaining row (id 1).
        List<MachineTelemetry> page2 = await repo.GetTelemetryPageByMachineIdsAndTypeAsync([1], 9, since, 2, 2, CancellationToken.None);
        await Assert.That(page2.Count).IsEqualTo(1);
        await Assert.That(page2[0].Id).IsEqualTo(1L);
    }

    [Test]
    public async Task GetTelemetryPageByMachineIdsAndType_EmptyMachineIds_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<MachineTelemetry> page = await repo.GetTelemetryPageByMachineIdsAndTypeAsync([], 9, DateTimeOffset.UtcNow.AddDays(-1), 0, 10, CancellationToken.None);
        int count = await repo.CountTelemetryByMachineIdsAndTypeAsync([], 9, DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);

        await Assert.That(page.Count).IsEqualTo(0);
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task CountTelemetryByMachineIdsAndType_OnlyCountsRowsWithinWindowAndType()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset since = now.AddDays(-1);

        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now, id: 1);
        await InsertTelemetryAsync(dbFactory, machineId: 2, telemetryType: 9, receivedAt: now, id: 2);
        await InsertTelemetryAsync(dbFactory, machineId: 3, telemetryType: 9, receivedAt: now, id: 3); // not in id set
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now.AddDays(-5), id: 4); // out of window

        int count = await repo.CountTelemetryByMachineIdsAndTypeAsync([1, 2], 9, since, CancellationToken.None);

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task CountTelemetryByMachineIdsAndType_WindowsOnServerReceivedAt_NotAgentClock()
    {
        // A forward/backward skewed agent clock must not decide window membership. The bounded window
        // filters on the server-stamped ServerReceivedAt, so an in-agent-window row whose ServerReceivedAt
        // is outside the window is excluded.
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset since = now.AddDays(-1);

        // Both rows carry an in-window ReceivedAt; only the first was actually received in-window.
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now, id: 1, serverReceivedAt: now);
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now, id: 2, serverReceivedAt: now.AddDays(-5));

        int count = await repo.CountTelemetryByMachineIdsAndTypeAsync([1], 9, since, CancellationToken.None);

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task GetTelemetryPageByMachineIdsAndType_OrdersByServerReceivedAt_NotAgentClock()
    {
        // Ordering is newest-first by server receipt time, so a row with a future agent clock does not
        // jump to the front of the page.
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // id 2 has the newest agent clock but an older server receipt; id 1 was received most recently.
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now.AddMinutes(-10), id: 1, serverReceivedAt: now);
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now.AddDays(3), id: 2, serverReceivedAt: now.AddMinutes(-5));

        List<MachineTelemetry> page = await repo.GetTelemetryPageByMachineIdsAndTypeAsync([1], 9, now.AddDays(-1), 0, 10, CancellationToken.None);

        await Assert.That(page.Count).IsEqualTo(2);
        await Assert.That(page[0].Id).IsEqualTo(1L);
        await Assert.That(page[1].Id).IsEqualTo(2L);
    }

    private static async Task InsertTelemetryAsync(TestDatabaseFactory dbFactory, long machineId, short telemetryType, DateTimeOffset receivedAt, long id, DateTimeOffset? serverReceivedAt = null)
    {
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            Id = id,
            MachineId = machineId,
            TenantId = 1,
            TelemetryType = telemetryType,
            Payload = """{"user":"x"}""",
            ReceivedAt = receivedAt,
            ServerReceivedAt = serverReceivedAt ?? receivedAt,
            SourceEventId = Guid.NewGuid().ToString("N"),
        });
    }

    private static async Task<int> SeedTenantAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);

        return await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);
    }

    // Machines are inserted in order, so the returned ids ascend in step with the given statuses.
    private static async Task<List<long>> SeedFleetMachinesAsync(TestDatabaseFactory dbFactory, int tenantId, short[] healthStatuses)
    {
        List<long> ids = [];
        foreach (short healthStatus in healthStatuses)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
            long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
            await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: tenantId, healthStatus: healthStatus));
            ids.Add(machineId);
        }

        return ids;
    }

    private static async Task<long> SeedFleetMachineAsync(TestDatabaseFactory dbFactory, int tenantId, short healthStatus, int securityUpdates)
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: tenantId, healthStatus: healthStatus);
        summary.SecurityUpdates = securityUpdates;
        await dbFactory.Context.InsertAsync(summary);

        return machineId;
    }

    // ========== Projection cursor tests ==========

    [Test]
    public async Task GetProjectionCursorAsync_NoRow_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        long? position = await repo.GetProjectionCursorAsync(0);

        await Assert.That(position).IsNull();
    }

    [Test]
    public async Task SetProjectionCursorAsync_FirstWrite_InsertsRow()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await repo.SetProjectionCursorAsync(0, 42, 1);

        long? position = await repo.GetProjectionCursorAsync(0);

        await Assert.That(position).IsEqualTo(42L);
    }

    [Test]
    public async Task SetProjectionCursorAsync_SecondWrite_UpdatesPosition()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await repo.SetProjectionCursorAsync(0, 42, 1);
        await repo.SetProjectionCursorAsync(0, 100, 1);

        long? position = await repo.GetProjectionCursorAsync(0);
        await Assert.That(position).IsEqualTo(100L);

        // A second write must update in place, not insert a duplicate row for the same shard.
        List<MachineStateProjectionCursor> rows = await dbFactory.Context.MachineStateProjectionCursors
            .Where(c => c.ShardIndex == 0)
            .ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ProjectionCursor_DistinctShards_AreIndependent()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await repo.SetProjectionCursorAsync(0, 10, 2);
        await repo.SetProjectionCursorAsync(1, 20, 2);

        await Assert.That(await repo.GetProjectionCursorAsync(0)).IsEqualTo(10L);
        await Assert.That(await repo.GetProjectionCursorAsync(1)).IsEqualTo(20L);

        // Advancing one shard must not move the other.
        await repo.SetProjectionCursorAsync(0, 11, 2);
        await Assert.That(await repo.GetProjectionCursorAsync(0)).IsEqualTo(11L);
        await Assert.That(await repo.GetProjectionCursorAsync(1)).IsEqualTo(20L);
    }

    [Test]
    public async Task GetPersistedShardCountAsync_NoCursors_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(await repo.GetPersistedShardCountAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task SetProjectionCursorAsync_PersistsShardCount_AndGetPersistedReturnsIt()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await repo.SetProjectionCursorAsync(0, 5, shardCount: 4);

        await Assert.That(await repo.GetPersistedShardCountAsync(CancellationToken.None)).IsEqualTo(4);
    }

    [Test]
    public async Task SetProjectionCursorAsync_DoubleWriteNewShard_DoesNotThrow()
    {
        // Two writes to a brand-new shard must upsert without a primary-key violation (the old
        // update-then-insert could race on the insert). Sequential double-call is an adequate SQLite
        // proxy; the real ON CONFLICT round-trip is asserted in the integration project.
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await repo.SetProjectionCursorAsync(0, 5, shardCount: 1);
        await repo.SetProjectionCursorAsync(0, 9, shardCount: 1);

        await Assert.That(await repo.GetProjectionCursorAsync(0)).IsEqualTo(9L);
    }

    // ========== RecordHeartbeatAsync tests ==========

    [Test]
    public async Task RecordHeartbeatAsync_SetsTheHeartbeatTimestamp()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int _, int _, long machineId) = await SeedMachineWithStateAsync(dbFactory);

        DateTimeOffset receivedAt = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
        await repo.RecordHeartbeatAsync(machineId, receivedAt);

        MachineStateSummary? updated = await repo.GetSummaryForMachineAsync(machineId);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.LastHeartbeatAt).IsEqualTo(receivedAt);
    }

    [Test]
    public async Task RecordHeartbeatAsync_NeverMovesTheTimestampBackward()
    {
        // A retried or delayed ping must not rewind liveness, the same rule LastSeenAt follows.
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int _, int _, long machineId) = await SeedMachineWithStateAsync(dbFactory);

        DateTimeOffset later = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset earlier = later.AddMinutes(-5);

        await repo.RecordHeartbeatAsync(machineId, later);
        await repo.RecordHeartbeatAsync(machineId, earlier);

        MachineStateSummary? updated = await repo.GetSummaryForMachineAsync(machineId);

        await Assert.That(updated!.LastHeartbeatAt).IsEqualTo(later);
    }

    [Test]
    public async Task RecordHeartbeatAsync_NoSummaryRow_LogsAWarning()
    {
        // A machine with no summary row cannot be marked live by any path, so a dropped heartbeat
        // would leave it reading Offline forever with nothing to explain why.
        using TestDatabaseFactory dbFactory = new();
        ILogger<Database.Repositories.DatabaseRepository> logger = Substitute.For<ILogger<Database.Repositories.DatabaseRepository>>();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, logger);

        await repo.RecordHeartbeatAsync(999999, new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero));

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
