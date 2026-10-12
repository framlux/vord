// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for hostname-fallback reconciliation: a report whose node name is only the
/// hostname applies to the reporter's row for the same machine id, and a reporter-sourced row is
/// unlinked, never deleted, when the machine renames or moves away from it.
/// </summary>
public class KubernetesHostnameFallbackRepositoryTests : KubernetesInventoryRepositoryTestBase
{
    private const string MachineIdOfControlPlane = "0123456789abcdef0123456789abcdef";

    [Test]
    public async Task ApplyLocalReport_HostnameFallback_MergesIntoTheReporterRowForTheSameMachineId()
    {
        // Intent: when the agent could not read the node's certificate name and sent its hostname, the
        // cluster reporter already knows the node under its real name and carries the machine's
        // /etc/machine-id. The report applies to that row, whose name is authoritative, instead of
        // adding a second node named after the hostname.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: MachineIdOfControlPlane);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long reporterRow = await SeedReporterNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1.example.com", MachineIdOfControlPlane, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5), nodeName: "host-a", controlPlane: true, nodeNameSource: "hostname"),
            T0.AddMinutes(5), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await Assert.That(node.Id).IsEqualTo(reporterRow);
        await Assert.That(node.NodeName).IsEqualTo("cp-1.example.com");
        await Assert.That(node.MachineId).IsEqualTo(machineId);
        await Assert.That(node.LastLocalReportAt).IsEqualTo(T0.AddMinutes(5));
        await Assert.That(node.LocalControlPlane).IsTrue();
        await Assert.That(node.KubeletVersion).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(node.ReportedCaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That(node.NodeMachineId).IsEqualTo(MachineIdOfControlPlane);
        await Assert.That(node.LastReporterSeenAt).IsEqualTo(T0);
        await Assert.That(node.PodCount).IsEqualTo(12);
    }

    [Test]
    public async Task ApplyLocalReport_HostnameFallback_MatchesTheMachineIdIgnoringCase()
    {
        // Intent: machine ids are hexadecimal text that different tools print in different cases, so the
        // match is case-insensitive.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: MachineIdOfControlPlane.ToUpperInvariant());
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long reporterRow = await SeedReporterNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", MachineIdOfControlPlane, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5), nodeName: "host-a", nodeNameSource: "hostname"),
            T0.AddMinutes(5), CancellationToken.None);

        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await Assert.That(node.Id).IsEqualTo(reporterRow);
        await Assert.That(node.MachineId).IsEqualTo(machineId);
    }

    [Test]
    public async Task ApplyLocalReport_HostnameFallbackAgain_StaysOnTheSameRow()
    {
        // Intent: once the machine is linked to the reporter's row, each later hostname report keeps
        // updating that row instead of splitting off a hostname-named node.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: MachineIdOfControlPlane);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long reporterRow = await SeedReporterNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", MachineIdOfControlPlane, T0);
        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5), nodeName: "host-a", nodeNameSource: "hostname"),
            T0.AddMinutes(5), CancellationToken.None);

        await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0.AddMinutes(20), nodeName: "host-a", nodeNameSource: "hostname"),
            T0.AddMinutes(20), CancellationToken.None);

        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await Assert.That(node.Id).IsEqualTo(reporterRow);
        await Assert.That(node.LastLocalReportAt).IsEqualTo(T0.AddMinutes(20));
    }

    [Test]
    public async Task ApplyLocalReport_HostnameFallback_ReplacesTheMachinesOwnHostnameRow()
    {
        // Intent: a machine that had been listed under its hostname before the reporter was enabled
        // moves onto the reporter's row, and its own local-only hostname row is removed so the node is
        // not listed twice.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: MachineIdOfControlPlane);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long hostnameRow = await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "host-a", machineId, FingerprintA, T0);
        long reporterRow = await SeedReporterNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", MachineIdOfControlPlane, T0.AddMinutes(1));

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5), nodeName: "host-a", nodeNameSource: "hostname"),
            T0.AddMinutes(5), CancellationToken.None);

        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.Id == hostnameRow)).IsEqualTo(0);
        KubernetesNode node = await dbFactory.Context.KubernetesNodes.SingleAsync();
        await Assert.That(node.Id).IsEqualTo(reporterRow);
        await Assert.That(node.MachineId).IsEqualTo(machineId);
    }

    [Test]
    public async Task ApplyLocalReport_CertificateName_IsNeverReconciled()
    {
        // Intent: only a name that fell back to the hostname is doubtful. A name read from the
        // certificate is the node's real name, so it gets its own row even when a reporter row carries
        // the machine's id.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: MachineIdOfControlPlane);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long reporterRow = await SeedReporterNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", MachineIdOfControlPlane, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5), nodeName: "cp-renamed", nodeNameSource: "certificate"),
            T0.AddMinutes(5), CancellationToken.None);

        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(2);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineId)).NodeName).IsEqualTo("cp-renamed");
        await Assert.That((await dbFactory.Context.KubernetesNodes.SingleAsync(n => n.Id == reporterRow)).MachineId).IsNull();
    }

    [Test]
    public async Task ApplyLocalReport_HostnameFallback_EmptySystemId_DoesNotReconcile()
    {
        // Intent: a machine with no recorded system id would otherwise match any reporter row whose
        // machine id is also empty, so an empty id never reconciles.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: string.Empty);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedReporterNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", string.Empty, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5), nodeName: "host-a", nodeNameSource: "hostname"),
            T0.AddMinutes(5), CancellationToken.None);

        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(2);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineId)).NodeName).IsEqualTo("host-a");
    }

    [Test]
    public async Task ApplyLocalReport_HostnameFallback_RowNoReporterHasSeen_DoesNotReconcile()
    {
        // Intent: only a row the cluster reporter has listed can carry the authoritative name. A row
        // that merely holds a machine id from before, with no sighting, is not that.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: MachineIdOfControlPlane);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedReporterNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", MachineIdOfControlPlane, null);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5), nodeName: "host-a", nodeNameSource: "hostname"),
            T0.AddMinutes(5), CancellationToken.None);

        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(2);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineId)).NodeName).IsEqualTo("host-a");
    }

    [Test]
    public async Task ApplyLocalReport_HostnameFallback_ReporterRowOfAnotherCluster_DoesNotReconcile()
    {
        // Intent: reconciliation looks only inside the machine's own cluster, so a node of another
        // cluster with the same machine id is not taken.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: MachineIdOfControlPlane);
        long home = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long other = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, T0);
        long foreignRow = await SeedReporterNodeAsync(dbFactory.Context, tenantId, other, "cp-1", MachineIdOfControlPlane, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5), nodeName: "host-a", nodeNameSource: "hostname"),
            T0.AddMinutes(5), CancellationToken.None);

        KubernetesNode node = await NodeOfMachineAsync(dbFactory.Context, machineId);
        await Assert.That(node.ClusterId).IsEqualTo(home);
        await Assert.That(node.NodeName).IsEqualTo("host-a");
        await Assert.That((await dbFactory.Context.KubernetesNodes.SingleAsync(n => n.Id == foreignRow)).MachineId).IsNull();
    }

    [Test]
    public async Task ApplyLocalReport_HostnameFallback_TwoReporterRowsWithTheMachineId_DoesNotReconcile()
    {
        // Intent: when two reporter rows carry the same machine id, as cloned virtual machines do, there
        // is no telling which one this machine is, so the report keeps its own hostname row.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: MachineIdOfControlPlane);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedReporterNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", MachineIdOfControlPlane, T0);
        await SeedReporterNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-2", MachineIdOfControlPlane, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5), nodeName: "host-a", nodeNameSource: "hostname"),
            T0.AddMinutes(5), CancellationToken.None);

        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(3);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineId)).NodeName).IsEqualTo("host-a");
    }

    [Test]
    public async Task ApplyLocalReport_HostnameFallback_SystemIdSharedByAnotherMachine_DoesNotReconcile()
    {
        // Intent: a system id that more than one machine in the tenant carries, which cloned machines
        // do, does not identify any of them. Reconciling would hand one reporter row back and forth
        // between the clones on every report, so neither takes it.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: MachineIdOfControlPlane);
        await SeedMachineAsync(dbFactory.Context, tenantId, systemId: MachineIdOfControlPlane);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long reporterRow = await SeedReporterNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", MachineIdOfControlPlane, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5), nodeName: "host-a", nodeNameSource: "hostname"),
            T0.AddMinutes(5), CancellationToken.None);

        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(2);
        await Assert.That((await dbFactory.Context.KubernetesNodes.SingleAsync(n => n.Id == reporterRow)).MachineId).IsNull();
    }

    [Test]
    public async Task ApplyLocalReport_HostnameFallback_SystemIdSharedOnlyWithADeletedMachine_StillReconciles()
    {
        // Intent: a deleted machine no longer competes for the id, so it does not stop the live
        // machine from taking its node.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: MachineIdOfControlPlane);
        await SeedMachineAsync(dbFactory.Context, tenantId, isDeleted: true, systemId: MachineIdOfControlPlane);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long reporterRow = await SeedReporterNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", MachineIdOfControlPlane, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5), nodeName: "host-a", nodeNameSource: "hostname"),
            T0.AddMinutes(5), CancellationToken.None);

        await Assert.That((await dbFactory.Context.KubernetesNodes.SingleAsync()).Id).IsEqualTo(reporterRow);
    }

    [Test]
    public async Task ApplyLocalReport_HostnameFallback_ReporterRowOfAnotherTenantsMachineId_IsIgnored()
    {
        // Intent: another tenant's cluster holding a node with this machine id is invisible to the
        // reconciliation, which only ever reads the machine's own tenant.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenant = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, systemId: MachineIdOfControlPlane);
        long otherCluster = await SeedClusterAsync(dbFactory.Context, otherTenant, FingerprintA, T0);
        long foreignRow = await SeedReporterNodeAsync(dbFactory.Context, otherTenant, otherCluster, "cp-1", MachineIdOfControlPlane, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5), nodeName: "host-a", nodeNameSource: "hostname"),
            T0.AddMinutes(5), CancellationToken.None);

        KubernetesNode node = await NodeOfMachineAsync(dbFactory.Context, machineId);
        await Assert.That(node.TenantId).IsEqualTo(tenantId);
        await Assert.That((await dbFactory.Context.KubernetesNodes.SingleAsync(n => n.Id == foreignRow)).MachineId).IsNull();
    }

    // ========== Reporter rows survive a rename ==========

    [Test]
    public async Task ApplyLocalReport_RenamedMachineWithAReporterSourcedOldRow_UnlinksTheOldRow()
    {
        // Intent: a local report never deletes a row the cluster reporter still sees. When the machine
        // moves to a new node name, the old row keeps its reporter-sourced values and loses only the
        // machine link, and the machine is linked to its new row.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long oldRow = await SeedReporterNodeAsync(
            dbFactory.Context, tenantId, clusterId, "host-a", "feedface", T0, machineId: machineId, lastLocalReportAt: T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0.AddMinutes(15), nodeName: "node-a.example.com"), T0.AddMinutes(15), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        KubernetesNode old = await dbFactory.Context.KubernetesNodes.SingleAsync(n => n.Id == oldRow);
        await Assert.That(old.MachineId).IsNull();
        await Assert.That(old.NodeName).IsEqualTo("host-a");
        await Assert.That(old.LastReporterSeenAt).IsEqualTo(T0);
        await Assert.That(old.NodeMachineId).IsEqualTo("feedface");
        KubernetesNode current = await NodeOfMachineAsync(dbFactory.Context, machineId);
        await Assert.That(current.Id).IsNotEqualTo(oldRow);
        await Assert.That(current.NodeName).IsEqualTo("node-a.example.com");
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(2);
    }

    [Test]
    public async Task ApplyLocalReport_MachineMovedToAnotherClusterFromAReporterSourcedRow_UnlinksTheOldRow()
    {
        // Intent: the same holds when the machine moves to another cluster: the cluster it left keeps
        // the node the reporter lists, now with no machine behind it.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long left = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long joined = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, T0);
        long oldRow = await SeedReporterNodeAsync(
            dbFactory.Context, tenantId, left, "node-1", "feedface", T0, machineId: machineId, lastLocalReportAt: T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0.AddMinutes(15), fingerprint: FingerprintB), T0.AddMinutes(15), CancellationToken.None);

        KubernetesNode old = await dbFactory.Context.KubernetesNodes.SingleAsync(n => n.Id == oldRow);
        await Assert.That(old.ClusterId).IsEqualTo(left);
        await Assert.That(old.MachineId).IsNull();
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineId)).ClusterId).IsEqualTo(joined);
    }
}
