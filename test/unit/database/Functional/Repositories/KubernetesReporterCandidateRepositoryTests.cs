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
/// Functional tests for the reporter candidate rows that local reports keep: a machine that reports
/// itself enrolled becomes a candidate of its node's cluster, and a machine with only a candidate row
/// keeps the cluster of that row.
/// </summary>
public class KubernetesReporterCandidateRepositoryTests : KubernetesInventoryRepositoryTestBase
{
    [Test]
    public async Task ApplyLocalReport_EnrolledMachine_CreatesACandidateInTheNodesCluster()
    {
        // Intent: a machine that reports itself enrolled as a cluster reporter is a candidate of the
        // cluster its node belongs to, stamped with the report's receipt time and carrying no error.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0, reporterEnrolled: true), T0, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.ClusterCreated);
        long clusterId = (await dbFactory.Context.KubernetesClusters.SingleAsync()).Id;
        KubernetesReporterCandidate candidate = await dbFactory.Context.KubernetesReporterCandidates.SingleAsync();
        await Assert.That(candidate.TenantId).IsEqualTo(tenantId);
        await Assert.That(candidate.ClusterId).IsEqualTo(clusterId);
        await Assert.That(candidate.MachineId).IsEqualTo(machineId);
        await Assert.That(candidate.LastReportAt).IsEqualTo(T0);
        await Assert.That(candidate.ReporterError).IsNull();
    }

    [Test]
    public async Task ApplyLocalReport_MachineNotEnrolled_NeverGetsACandidate()
    {
        // Intent: only a machine that reports itself enrolled is a candidate, so an ordinary node adds
        // no candidate row.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);

        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, machineId, T0), T0, CancellationToken.None);

        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(1);
        await Assert.That(await dbFactory.Context.KubernetesReporterCandidates.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task ApplyLocalReport_RepeatedEnrolledReports_UpdateOneCandidateRow()
    {
        // Intent: a candidate is one row per machine that follows its latest report, not a row per
        // report.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, machineId, T0, reporterEnrolled: true), T0, CancellationToken.None);

        await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0.AddMinutes(15), reporterEnrolled: true), T0.AddMinutes(15), CancellationToken.None);

        KubernetesReporterCandidate candidate = await dbFactory.Context.KubernetesReporterCandidates.SingleAsync();
        await Assert.That(candidate.LastReportAt).IsEqualTo(T0.AddMinutes(15));
    }

    [Test]
    public async Task ApplyLocalReport_NoLongerEnrolled_DeletesTheMachinesCandidateOnly()
    {
        // Intent: a machine that reports it is not enrolled any more stops being a candidate, while
        // another machine's candidate row in the same cluster is left alone.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long sibling = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0, nodeName: "node-1", reporterEnrolled: true), T0, CancellationToken.None);
        await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, sibling, T0, nodeName: "node-2", reporterEnrolled: true), T0, CancellationToken.None);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(3, tenantId, machineId, T0.AddMinutes(15), nodeName: "node-1", reporterEnrolled: false),
            T0.AddMinutes(15), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        KubernetesReporterCandidate remaining = await dbFactory.Context.KubernetesReporterCandidates.SingleAsync();
        await Assert.That(remaining.MachineId).IsEqualTo(sibling);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(2);
    }

    [Test]
    [Arguments("credentials_rejected")]
    [Arguments("tls_ca_changed")]
    [Arguments("state_copied")]
    public async Task ApplyLocalReport_ReporterError_IsStoredOnTheCandidateAndClearedByTheNextReport(string reporterError)
    {
        // Intent: every candidate reports its own token and TLS failures through its local report, so
        // the error is stored on the candidate row as reported and a later report without one clears
        // it again.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0, reporterEnrolled: true, reporterError: reporterError), T0, CancellationToken.None);
        string? stored = (await dbFactory.Context.KubernetesReporterCandidates.SingleAsync()).ReporterError;
        await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0.AddMinutes(15), reporterEnrolled: true), T0.AddMinutes(15), CancellationToken.None);
        string? cleared = (await dbFactory.Context.KubernetesReporterCandidates.SingleAsync()).ReporterError;

        await Assert.That(stored).IsEqualTo(reporterError);
        await Assert.That(cleared).IsNull();
    }

    [Test]
    public async Task ApplyLocalReport_CandidateInAnotherCluster_MovesToTheNodesCluster()
    {
        // Intent: a machine is a candidate of one cluster only, so when its node belongs to a different
        // cluster than its candidate row, the row follows the node instead of a second one appearing.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long home = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long other = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, home, "node-1", machineId, FingerprintA, T0);
        await SeedCandidateAsync(dbFactory.Context, tenantId, other, machineId, T0, "tls_ca_changed");

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(15), reporterEnrolled: true), T0.AddMinutes(15), CancellationToken.None);

        KubernetesReporterCandidate candidate = await dbFactory.Context.KubernetesReporterCandidates.SingleAsync();
        await Assert.That(candidate.ClusterId).IsEqualTo(home);
        await Assert.That(candidate.MachineId).IsEqualTo(machineId);
        await Assert.That(candidate.LastReportAt).IsEqualTo(T0.AddMinutes(15));
        await Assert.That(candidate.ReporterError).IsNull();
    }

    [Test]
    public async Task ApplyLocalReport_NodeMovesToAnotherCluster_TakesItsCandidateAlong()
    {
        // Intent: when the machine moves to the one other cluster that holds the CA it now reports, its
        // candidate row moves with the node and the cluster it left keeps no candidate for it.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long left = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long joined = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, left, "node-1", machineId, FingerprintA, T0);
        await SeedCandidateAsync(dbFactory.Context, tenantId, left, machineId, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(15), fingerprint: FingerprintB, reporterEnrolled: true),
            T0.AddMinutes(15), CancellationToken.None);

        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineId)).ClusterId).IsEqualTo(joined);
        KubernetesReporterCandidate candidate = await dbFactory.Context.KubernetesReporterCandidates.SingleAsync();
        await Assert.That(candidate.ClusterId).IsEqualTo(joined);
    }

    [Test]
    public async Task ApplyLocalReport_OlderThanTheCandidate_IsSupersededAndKeepsTheCandidate()
    {
        // Intent: a report that arrives after a newer one must not roll the candidate back, neither by
        // overwriting its error nor by deleting it because the older report said not enrolled.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "node-1", machineId, FingerprintA, T0);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, machineId, T0.AddMinutes(10), "credentials_rejected");

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(5)), T0.AddMinutes(20), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Superseded);
        KubernetesReporterCandidate candidate = await dbFactory.Context.KubernetesReporterCandidates.SingleAsync();
        await Assert.That(candidate.LastReportAt).IsEqualTo(T0.AddMinutes(10));
        await Assert.That(candidate.ReporterError).IsEqualTo("credentials_rejected");
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(1L);
    }

    [Test]
    public async Task ApplyLocalReport_SupersededReportOfANodeWithACandidate_LeavesBothRowsAlone()
    {
        // Intent: the same ordering rule covers a report older than the node itself: neither the node
        // nor the candidate takes anything from it.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(10), reporterEnrolled: true), T0.AddMinutes(10), CancellationToken.None);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, machineId, T0, reporterEnrolled: false), T0.AddMinutes(11), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Superseded);
        await Assert.That(await dbFactory.Context.KubernetesReporterCandidates.CountAsync()).IsEqualTo(1);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineId)).LastLocalReportAt).IsEqualTo(T0.AddMinutes(10));
    }

    // ========== Established membership through a candidate ==========

    [Test]
    public async Task ApplyLocalReport_MachineWithOnlyACandidateRow_JoinsThatCluster()
    {
        // Intent: a candidate row is established membership. A controller that gains a kubelet joins the
        // cluster it is already a candidate of, even though two clusters share its CA and the
        // fingerprint alone would leave it ambiguous.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long home = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long twin = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedCandidateAsync(dbFactory.Context, tenantId, home, machineId, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(15), nodeName: "cp-1", reporterEnrolled: true),
            T0.AddMinutes(15), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        KubernetesNode node = await NodeOfMachineAsync(dbFactory.Context, machineId);
        await Assert.That(node.ClusterId).IsEqualTo(home);
        await Assert.That(node.NodeName).IsEqualTo("cp-1");
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync(n => n.ClusterId == twin)).IsEqualTo(0);
        await Assert.That((await dbFactory.Context.KubernetesReporterCandidates.SingleAsync()).ClusterId).IsEqualTo(home);
    }

    [Test]
    public async Task ApplyLocalReport_MachineWithOnlyACandidateRow_NoFingerprint_StillJoinsItsCluster()
    {
        // Intent: like a node, a candidate keeps its place when a report carries no fingerprint.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long home = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedCandidateAsync(dbFactory.Context, tenantId, home, machineId, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(15), fingerprint: null, reporterEnrolled: true),
            T0.AddMinutes(15), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Grouped);
        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineId)).ClusterId).IsEqualTo(home);
    }

    [Test]
    public async Task ApplyLocalReport_MachineWithOnlyACandidateRow_MovesToTheOneClusterHoldingItsNewCa()
    {
        // Intent: the move rule that frees a node from its cluster applies to a candidate's membership
        // the same way, and the candidate row follows the machine.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long left = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        long joined = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, T0);
        await SeedCandidateAsync(dbFactory.Context, tenantId, left, machineId, T0);

        await repo.ApplyLocalNodeReportAsync(
            Report(1, tenantId, machineId, T0.AddMinutes(15), fingerprint: FingerprintB, reporterEnrolled: true),
            T0.AddMinutes(15), CancellationToken.None);

        await Assert.That((await NodeOfMachineAsync(dbFactory.Context, machineId)).ClusterId).IsEqualTo(joined);
        await Assert.That((await dbFactory.Context.KubernetesReporterCandidates.SingleAsync()).ClusterId).IsEqualTo(joined);
    }
}
