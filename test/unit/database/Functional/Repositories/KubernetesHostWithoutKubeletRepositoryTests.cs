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
/// Functional tests for the reports of hosts that run no kubelet: such a host is never a node and
/// never founds a cluster, but an enrolled one, as a k0s controller is, can still be a reporter
/// candidate.
/// </summary>
public class KubernetesHostWithoutKubeletRepositoryTests : KubernetesInventoryRepositoryTestBase
{
    [Test]
    public async Task ApplyLocalReport_NoKubeletAndNotEnrolled_DeletesTheMachinesCandidateAndWritesNothingElse()
    {
        // Intent: a host that runs no kubelet and says it is not enrolled is not a node and not a
        // candidate any more. Its stale candidate row goes, another machine's stays, and nothing else
        // is written but the cursor.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long sibling = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, machineId, T0);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, sibling, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(9, tenantId, machineId, T0.AddMinutes(15), hasKubelet: false, reporterEnrolled: false),
            T0.AddMinutes(15), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.NotANode);
        KubernetesReporterCandidate remaining = await dbFactory.Context.KubernetesReporterCandidates.SingleAsync();
        await Assert.That(remaining.MachineId).IsEqualTo(sibling);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(0);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(9L);
    }

    [Test]
    public async Task ApplyLocalReport_NoKubeletAndNotEnrolled_OlderThanTheCandidate_IsSuperseded()
    {
        // Intent: a not-enrolled report that was recorded before a newer enrolled one must not delete
        // the newer candidate.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, machineId, T0.AddMinutes(10));

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(9, tenantId, machineId, T0.AddMinutes(5), hasKubelet: false), T0.AddMinutes(20), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.Superseded);
        await Assert.That(await dbFactory.Context.KubernetesReporterCandidates.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task ApplyLocalReport_K0sController_BecomesACandidateWithoutANode()
    {
        // Intent: a control-plane host with no kubelet, which is what a k0s controller is, cannot be a
        // node, but when it is enrolled and exactly one cluster holds its CA it is that cluster's
        // candidate. It adds no node row, no cluster, and the cluster's own state is untouched.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long worker = await SeedMachineAsync(dbFactory.Context, tenantId);
        long controller = await SeedMachineAsync(dbFactory.Context, tenantId);
        await repo.ApplyLocalNodeReportAsync(Report(1, tenantId, worker, T0, nodeName: "worker-1", distro: "k0s"), T0, CancellationToken.None);
        KubernetesCluster before = await dbFactory.Context.KubernetesClusters.SingleAsync();

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, controller, T0.AddMinutes(5), nodeName: string.Empty, hasKubelet: false, controlPlane: true,
                distro: "k0s", reporterEnrolled: true, reporterError: "credentials_rejected"),
            T0.AddMinutes(5), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.CandidateOnly);
        KubernetesReporterCandidate candidate = await dbFactory.Context.KubernetesReporterCandidates.SingleAsync();
        await Assert.That(candidate.ClusterId).IsEqualTo(before.Id);
        await Assert.That(candidate.MachineId).IsEqualTo(controller);
        await Assert.That(candidate.TenantId).IsEqualTo(tenantId);
        await Assert.That(candidate.LastReportAt).IsEqualTo(T0.AddMinutes(5));
        await Assert.That(candidate.ReporterError).IsEqualTo("credentials_rejected");
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(1);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(2L);
    }

    [Test]
    public async Task ApplyLocalReport_CandidateOnlyMachine_UpdatesItsExistingCandidateRow()
    {
        // Intent: a controller reports every cycle, so the second report refreshes the same candidate
        // row and replaces its error instead of adding a row.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long controller = await SeedMachineAsync(dbFactory.Context, tenantId);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, controller, T0, "credentials_rejected");

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, controller, T0.AddMinutes(15), hasKubelet: false, reporterEnrolled: true),
            T0.AddMinutes(15), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.CandidateOnly);
        KubernetesReporterCandidate candidate = await dbFactory.Context.KubernetesReporterCandidates.SingleAsync();
        await Assert.That(candidate.LastReportAt).IsEqualTo(T0.AddMinutes(15));
        await Assert.That(candidate.ReporterError).IsNull();
    }

    [Test]
    public async Task ApplyLocalReport_CandidateOnlyMachine_KeepsTheClusterOfItsCandidateRowOverTheFingerprint()
    {
        // Intent: a candidate row is membership, so a host without a kubelet stays in the cluster it
        // is already a candidate of even when its fingerprint now points at a different cluster.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long controller = await SeedMachineAsync(dbFactory.Context, tenantId);
        long home = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, T0);
        await SeedCandidateAsync(dbFactory.Context, tenantId, home, controller, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, controller, T0.AddMinutes(15), fingerprint: FingerprintB, hasKubelet: false, reporterEnrolled: true),
            T0.AddMinutes(15), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.CandidateOnly);
        await Assert.That((await dbFactory.Context.KubernetesReporterCandidates.SingleAsync()).ClusterId).IsEqualTo(home);
    }

    [Test]
    public async Task ApplyLocalReport_NoKubeletEnrolled_FingerprintHeldByTwoClusters_WritesNothing()
    {
        // Intent: a host with no kubelet is placed only by an unambiguous fingerprint. With a shared
        // CA it has no node name to tell the clusters apart, so it is not guessed into one.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long controller = await SeedMachineAsync(dbFactory.Context, tenantId);
        await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);
        await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, controller, T0, hasKubelet: false, reporterEnrolled: true), T0, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.NotANode);
        await Assert.That(await dbFactory.Context.KubernetesReporterCandidates.CountAsync()).IsEqualTo(0);
    }

    [Test]
    [Arguments(FingerprintB)]
    [Arguments(null)]
    public async Task ApplyLocalReport_NoKubeletEnrolled_NoClusterResolves_NeverCreatesACluster(string? fingerprint)
    {
        // Intent: a cluster is founded only by a host that runs a kubelet. A controller whose
        // fingerprint no cluster holds, or that reports none, writes nothing at all.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long controller = await SeedMachineAsync(dbFactory.Context, tenantId);
        await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, controller, T0, fingerprint: fingerprint, hasKubelet: false, reporterEnrolled: true),
            T0, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.NotANode);
        await Assert.That(await dbFactory.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
        await Assert.That(await dbFactory.Context.KubernetesNodes.CountAsync()).IsEqualTo(0);
        await Assert.That(await dbFactory.Context.KubernetesReporterCandidates.CountAsync()).IsEqualTo(0);
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(2L);
    }

    [Test]
    public async Task ApplyLocalReport_NoKubeletEnrolled_IgnoresAnotherTenantsClusterWithTheSameFingerprint()
    {
        // Intent: tenants never share clusters, so a controller is never made the candidate of another
        // tenant's cluster because both happen to hold the same CA.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenant = await SeedTenantAsync(dbFactory.Context);
        long controller = await SeedMachineAsync(dbFactory.Context, tenantId);
        await SeedClusterAsync(dbFactory.Context, otherTenant, FingerprintA, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, controller, T0, hasKubelet: false, reporterEnrolled: true), T0, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.NotANode);
        await Assert.That(await dbFactory.Context.KubernetesReporterCandidates.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task ApplyLocalReport_NoKubeletEnrolled_DeletedMachine_WritesNothing()
    {
        // Intent: a deleted machine never becomes a candidate, whatever its last report said.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = CreateRepo(dbFactory);
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long controller = await SeedMachineAsync(dbFactory.Context, tenantId, isDeleted: true);
        await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, T0);

        LocalReportOutcome outcome = await repo.ApplyLocalNodeReportAsync(
            Report(2, tenantId, controller, T0, hasKubelet: false, reporterEnrolled: true), T0, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.MachineDeleted);
        await Assert.That(await dbFactory.Context.KubernetesReporterCandidates.CountAsync()).IsEqualTo(0);
    }
}
