// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Models.Kubernetes;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="KubernetesClusterHandler"/>: the tenant-scoped cluster reads and the audited
/// rename.
/// </summary>
public class KubernetesClusterHandlerTests
{
    private const string FingerprintA = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";
    private const string FingerprintB = "0f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a6978";

    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = T0.AddMinutes(30);

    private static KubernetesClusterHandler CreateHandler(
        TestDatabaseFactory dbFactory,
        IAuditLogRepository? auditLog = null,
        IMachinePingService? pingService = null,
        IKubernetesInventoryRepository? kubernetesRepo = null)
    {
        DatabaseRepository repo = new(dbFactory.Context, NullLogger<DatabaseRepository>.Instance);
        FakeTimeProvider clock = new(Now);
        KubernetesReporterMachineReader machines = new(
            kubernetesRepo ?? repo,
            repo,
            repo,
            repo,
            pingService ?? new InMemoryMachinePingService(),
            TestQuotaPause.Nothing(),
            TestMetricsFactory.CreateQuotaPauseMetrics(),
            TestMetricsFactory.CreateKubernetesInventoryMetrics(),
            clock,
            NullLogger<KubernetesReporterMachineReader>.Instance);

        return new KubernetesClusterHandler(kubernetesRepo ?? repo, machines, repo, auditLog ?? repo, clock);
    }

    private static KubernetesReporterMachineReader SubstituteReader(IKubernetesInventoryRepository kubernetesRepo)
    {
        return new KubernetesReporterMachineReader(
            kubernetesRepo,
            Substitute.For<IMachineRepository>(),
            Substitute.For<IMachineStateRepository>(),
            Substitute.For<ISigningKeyRepository>(),
            Substitute.For<IMachinePingService>(),
            TestQuotaPause.Nothing(),
            TestMetricsFactory.CreateQuotaPauseMetrics(),
            TestMetricsFactory.CreateKubernetesInventoryMetrics(),
            new FakeTimeProvider(Now),
            NullLogger<KubernetesReporterMachineReader>.Instance);
    }

    private static async Task<int> SeedTenantAsync(DatabaseContext db)
    {
        return await db.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant());
    }

    private static async Task<long> SeedMachineAsync(DatabaseContext db, int tenantId, string name)
    {
        return await db.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId, hostname: name));
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
            LastSeenAt = T0.AddMinutes(5),
        });
    }

    private static async Task<long> SeedNodeAsync(
        DatabaseContext db,
        int tenantId,
        long clusterId,
        string nodeName,
        long? machineId = null,
        bool controlPlane = false,
        string? kubeletVersion = null,
        bool mismatch = false)
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = nodeName,
            MachineId = machineId,
            LocalControlPlane = controlPlane,
            KubeletVersion = kubeletVersion,
            FingerprintMismatch = mismatch,
            LastLocalReportAt = T0.AddMinutes(1),
        });
    }

    // ========== ListAsync ==========

    [Test]
    public async Task ListAsync_NullTenant_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await handler.ListAsync(null, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListAsync_SummarisesEachClusterOfTheTenantOnly()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long machineA = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-host");
        long machineB = await SeedMachineAsync(dbFactory.Context, tenantId, "worker-host");
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, distro: "rke2");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "worker-1", machineB, kubeletVersion: "v1.30.4+rke2r1");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", machineA, controlPlane: true, kubeletVersion: "v1.30.5+rke2r1");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "worker-2", null, kubeletVersion: "v1.30.4+rke2r1");
        long otherClusterId = await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA);
        await SeedNodeAsync(dbFactory.Context, otherTenantId, otherClusterId, "intruder");
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Count).IsEqualTo(1);
        KubernetesClusterSummaryDto summary = result.Data[0];
        await Assert.That(summary.Id).IsEqualTo(clusterId);
        await Assert.That(summary.Name).IsEqualTo("RKE2 cp-1");
        await Assert.That(summary.DisplayName).IsNull();
        await Assert.That(summary.Distro).IsEqualTo("rke2");
        await Assert.That(summary.NodeCount).IsEqualTo(3);
        await Assert.That(summary.ManagedNodeCount).IsEqualTo(2);
        await Assert.That(summary.UnmanagedNodeCount).IsEqualTo(1);
        await Assert.That(summary.KubeletVersions.ToArray()).IsEquivalentTo(new[] { "v1.30.4+rke2r1", "v1.30.5+rke2r1" });
        await Assert.That(summary.KubeletVersions[0]).IsEqualTo("v1.30.4+rke2r1");
        await Assert.That(summary.ReportingState).IsEqualTo("off");
        await Assert.That(summary.FirstSeenAt).IsEqualTo(T0);
        await Assert.That(summary.LastSeenAt).IsEqualTo(T0.AddMinutes(5));
    }

    [Test]
    public async Task ListAsync_TwoClusters_AttributesEachNodeToItsOwnCluster()
    {
        // Intent: a list of several clusters loads their nodes in one query, so each cluster must be
        // summarised from its own nodes only. Giving the clusters different node counts, versions and
        // control planes means a node counted for the wrong cluster changes at least one summary.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machine = await SeedMachineAsync(dbFactory.Context, tenantId, "edge-host");
        long first = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, distro: "k3s");
        long second = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintB, distro: "kubeadm");
        await SeedNodeAsync(dbFactory.Context, tenantId, first, "edge-1", machine, controlPlane: true, kubeletVersion: "v1.30.4+k3s1");
        await SeedNodeAsync(dbFactory.Context, tenantId, second, "prod-cp", controlPlane: true, kubeletVersion: "v1.31.0");
        await SeedNodeAsync(dbFactory.Context, tenantId, second, "prod-w1", kubeletVersion: "v1.30.9");
        await SeedNodeAsync(dbFactory.Context, tenantId, second, "prod-w2", kubeletVersion: "v1.30.9");
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Count).IsEqualTo(2);
        KubernetesClusterSummaryDto firstSummary = result.Data.Single(c => c.Id == first);
        await Assert.That(firstSummary.Name).IsEqualTo("k3s edge-1");
        await Assert.That(firstSummary.NodeCount).IsEqualTo(1);
        await Assert.That(firstSummary.ManagedNodeCount).IsEqualTo(1);
        await Assert.That(firstSummary.UnmanagedNodeCount).IsEqualTo(0);
        await Assert.That(firstSummary.KubeletVersions.ToArray()).IsEquivalentTo(new[] { "v1.30.4+k3s1" });
        KubernetesClusterSummaryDto secondSummary = result.Data.Single(c => c.Id == second);
        await Assert.That(secondSummary.Name).IsEqualTo("kubeadm prod-cp");
        await Assert.That(secondSummary.NodeCount).IsEqualTo(3);
        await Assert.That(secondSummary.ManagedNodeCount).IsEqualTo(0);
        await Assert.That(secondSummary.UnmanagedNodeCount).IsEqualTo(3);
        await Assert.That(secondSummary.KubeletVersions.ToArray()).IsEquivalentTo(new[] { "v1.30.9", "v1.31.0" });
    }

    [Test]
    public async Task ListAsync_DisplayNameWinsOverTheDerivedName()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "Production");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", controlPlane: true);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        await Assert.That(result.Data![0].Name).IsEqualTo("Production");
        await Assert.That(result.Data[0].DisplayName).IsEqualTo("Production");
    }

    [Test]
    public async Task ListAsync_ClusterWithoutNodes_IsNamedAfterTheFingerprintAndCountsNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        KubernetesClusterSummaryDto summary = result.Data![0];
        await Assert.That(summary.Name).IsEqualTo("k3s a1b2c3d4e5f6");
        await Assert.That(summary.NodeCount).IsEqualTo(0);
        await Assert.That(summary.ManagedNodeCount).IsEqualTo(0);
        await Assert.That(summary.UnmanagedNodeCount).IsEqualTo(0);
        await Assert.That(summary.KubeletVersions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListAsync_KubeletVersions_AreDistinctNonEmptyAndOrdinalSorted()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "n1", kubeletVersion: "v1.30.5");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "n2", kubeletVersion: "v1.30.4");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "n3", kubeletVersion: "v1.30.5");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "n4", kubeletVersion: "");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "n5", kubeletVersion: null);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        await Assert.That(string.Join(",", result.Data![0].KubeletVersions)).IsEqualTo("v1.30.4,v1.30.5");
    }

    // ========== GetAsync ==========

    [Test]
    public async Task GetAsync_NullTenant_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterDetailDto> result = await handler.GetAsync(1, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetAsync_UnknownCluster_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterDetailDto> result = await handler.GetAsync(987654, tenantId, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetAsync_AnotherTenantsCluster_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long foreignClusterId = await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintA);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterDetailDto> result = await handler.GetAsync(foreignClusterId, tenantId, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
        await Assert.That(result.Data).IsNull();
    }

    [Test]
    public async Task GetAsync_ReturnsSummaryFieldsFingerprintAndNodesInOrdinalNameOrder()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineId = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-host");
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long workerId = await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "worker-1", kubeletVersion: "v1.30.4");
        long cpId = await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", machineId, controlPlane: true, kubeletVersion: "v1.30.5");
        long upperId = await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "Zulu");
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterDetailDto> result = await handler.GetAsync(clusterId, tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        KubernetesClusterDetailDto detail = result.Data!;
        await Assert.That(detail.Id).IsEqualTo(clusterId);
        await Assert.That(detail.Name).IsEqualTo("k3s cp-1");
        await Assert.That(detail.CaFingerprint).IsEqualTo(FingerprintA);
        await Assert.That(detail.HasFingerprintMismatch).IsFalse();
        await Assert.That(detail.NodeCount).IsEqualTo(3);
        await Assert.That(detail.ReportingState).IsEqualTo("off");
        await Assert.That(detail.Nodes.Select(n => n.NodeName).ToArray()).IsEquivalentTo(new[] { "Zulu", "cp-1", "worker-1" });
        await Assert.That(detail.Nodes[0].NodeName).IsEqualTo("Zulu");
        await Assert.That(detail.Nodes[1].NodeName).IsEqualTo("cp-1");
        await Assert.That(detail.Nodes[2].NodeName).IsEqualTo("worker-1");

        KubernetesNodeDto cp = detail.Nodes.Single(n => n.Id == cpId);
        await Assert.That(cp.ControlPlane).IsTrue();
        await Assert.That(cp.KubeletVersion).IsEqualTo("v1.30.5");
        await Assert.That(cp.MachineId).IsEqualTo(machineId);
        await Assert.That(cp.MachineName).IsEqualTo("cp-host");
        await Assert.That(cp.FingerprintMismatch).IsFalse();
        await Assert.That(cp.LastLocalReportAt).IsEqualTo(T0.AddMinutes(1));

        KubernetesNodeDto worker = detail.Nodes.Single(n => n.Id == workerId);
        await Assert.That(worker.ControlPlane).IsFalse();
        await Assert.That(worker.MachineId).IsNull();
        await Assert.That(worker.MachineName).IsNull();
        await Assert.That(detail.Nodes.Any(n => n.Id == upperId)).IsTrue();
    }

    [Test]
    public async Task GetAsync_AnyNodeFlaggedMismatch_FlagsTheCluster()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "worker-1", mismatch: true);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterDetailDto> result = await handler.GetAsync(clusterId, tenantId, CancellationToken.None);

        await Assert.That(result.Data!.HasFingerprintMismatch).IsTrue();
        await Assert.That(result.Data.Nodes.Single(n => n.NodeName == "worker-1").FingerprintMismatch).IsTrue();
        await Assert.That(result.Data.Nodes.Single(n => n.NodeName == "cp-1").FingerprintMismatch).IsFalse();
    }

    // ========== Reporting: seeding ==========

    private static async Task<long> SeedReportingClusterAsync(
        DatabaseContext db,
        int tenantId,
        string fingerprint,
        long? holder = null,
        DateTimeOffset? lastSnapshot = null,
        string? error = null,
        bool roleOutdated = false,
        string? version = null,
        bool truncated = false,
        string? uid = null)
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = fingerprint,
            Distro = "k3s",
            FirstSeenAt = T0,
            LastSeenAt = T0.AddMinutes(5),
            ActiveReporterMachineId = holder,
            LastSnapshotReceivedAt = lastSnapshot,
            ReporterError = error,
            RoleOutdated = roleOutdated,
            ClusterVersion = version,
            Truncated = truncated,
            KubeSystemUid = uid,
        });
    }

    private static async Task SeedCandidateAsync(
        DatabaseContext db, int tenantId, long clusterId, long machineId, string? error = null, DateTimeOffset? lastReport = null)
    {
        await db.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            MachineId = machineId,
            ReporterError = error,
            LastReportAt = lastReport ?? T0.AddMinutes(10),
        });
    }

    private static async Task SeedReadyNodeAsync(
        DatabaseContext db, int tenantId, long clusterId, string name, string? readyStatus, string? roles = null)
    {
        await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = name,
            ReadyStatus = readyStatus,
            Roles = roles,
            LastReporterSeenAt = T0.AddMinutes(10),
        });
    }

    private static string LocalReportJson(string fingerprint, bool controlPlane = true, bool adminCredential = true, bool enrolled = false)
    {
        return $$"""
            {"distro":"k3s","kubelet_version":"v1.30.4+k3s1","node_name":"cp-1","node_name_source":"certificate","has_kubelet":true,"control_plane":{{(controlPlane ? "true" : "false")}},"ca_fingerprint":"{{fingerprint}}","admin_credential_present":{{(adminCredential ? "true" : "false")}},"reporter_enrolled":{{(enrolled ? "true" : "false")}}}
            """;
    }

    private static async Task SeedLocalReportAsync(DatabaseContext db, int tenantId, long machineId, string payload, DateTimeOffset? at = null)
    {
        await db.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachineTelemetry(
            machineId: machineId, tenantId: tenantId, telemetryType: 14, payload: payload, receivedAt: at ?? T0));
    }

    // ========== ListAsync: reporting ==========

    [Test]
    public async Task ListAsync_ReportingCluster_SummarisesStateHolderVersionAndNodeCounts()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long holder = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-host");
        long second = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-two");
        DateTimeOffset snapshot = Now.AddMinutes(-10);
        long clusterId = await SeedReportingClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, holder, snapshot, version: "v1.30.4+k3s1", truncated: true);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, holder);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, second);
        await SeedReadyNodeAsync(dbFactory.Context, tenantId, clusterId, "n1", "True");
        await SeedReadyNodeAsync(dbFactory.Context, tenantId, clusterId, "n2", "True");
        await SeedReadyNodeAsync(dbFactory.Context, tenantId, clusterId, "n3", "False");
        await SeedReadyNodeAsync(dbFactory.Context, tenantId, clusterId, "n4", "Unknown");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "n5");
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        KubernetesClusterSummaryDto summary = result.Data!.Single();
        await Assert.That(summary.ReportingState).IsEqualTo("active");
        await Assert.That(summary.ActiveReporterMachineId).IsEqualTo(holder);
        await Assert.That(summary.ActiveReporterMachineName).IsEqualTo("cp-host");
        await Assert.That(summary.ClusterVersion).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(summary.Truncated).IsTrue();
        await Assert.That(summary.LastSnapshotReceivedAt).IsEqualTo(snapshot);
        await Assert.That(summary.StaleSince).IsNull();
        await Assert.That(summary.ReporterError).IsNull();
        await Assert.That(summary.ReadyCount).IsEqualTo(2);
        await Assert.That(summary.NotReadyCount).IsEqualTo(1);
        await Assert.That(summary.UnknownCount).IsEqualTo(2);
    }

    [Test]
    public async Task ListAsync_SeveralClusters_EachGetsItsOwnStateFromItsOwnCandidates()
    {
        // Intent: candidates are loaded for all clusters at once, so each must be matched back to its own
        // cluster. Four clusters in four different states make a candidate attributed to the wrong
        // cluster change at least one state.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machineA = await SeedMachineAsync(dbFactory.Context, tenantId, "a-host");
        long machineB = await SeedMachineAsync(dbFactory.Context, tenantId, "b-host");
        long machineC = await SeedMachineAsync(dbFactory.Context, tenantId, "c-host");
        long active = await SeedReportingClusterAsync(dbFactory.Context, tenantId, FingerprintA, machineA, Now.AddMinutes(-1));
        long stale = await SeedReportingClusterAsync(dbFactory.Context, tenantId, FingerprintB, machineB, Now.AddHours(-4));
        long errored = await SeedReportingClusterAsync(dbFactory.Context, tenantId, "c" + FingerprintA[1..], machineC, Now.AddMinutes(-1));
        long off = await SeedReportingClusterAsync(dbFactory.Context, tenantId, "d" + FingerprintA[1..]);
        await SeedCandidateAsync(dbFactory.Context, tenantId, active, machineA);
        await SeedCandidateAsync(dbFactory.Context, tenantId, stale, machineB);
        await SeedCandidateAsync(dbFactory.Context, tenantId, errored, machineC, error: "credentials_rejected");
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        List<KubernetesClusterSummaryDto> summaries = result.Data!;
        await Assert.That(summaries.Single(c => c.Id == active).ReportingState).IsEqualTo("active");
        await Assert.That(summaries.Single(c => c.Id == stale).ReportingState).IsEqualTo("stale");
        await Assert.That(summaries.Single(c => c.Id == stale).StaleSince).IsEqualTo(Now.AddHours(-4));
        KubernetesClusterSummaryDto erroredSummary = summaries.Single(c => c.Id == errored);
        await Assert.That(erroredSummary.ReportingState).IsEqualTo("error");
        await Assert.That(erroredSummary.ReporterError).IsEqualTo("credentials_rejected");
        await Assert.That(summaries.Single(c => c.Id == off).ReportingState).IsEqualTo("off");
    }

    [Test]
    public async Task ListAsync_OtherTenantsReportingData_NeverReachesTheSummary()
    {
        // Intent: two tenants whose clusters share a CA fingerprint stay entirely separate. The other
        // tenant's candidates, error, holder and machine names must neither change this tenant's state
        // nor appear in its response.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long ownCluster = await SeedReportingClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long foreignMachine = await SeedMachineAsync(dbFactory.Context, otherTenantId, "foreign-secret-host");
        long foreignCluster = await SeedReportingClusterAsync(
            dbFactory.Context, otherTenantId, FingerprintA, foreignMachine, Now, error: "tls_ca_changed", version: "v9.99.9");
        await SeedCandidateAsync(dbFactory.Context, otherTenantId, foreignCluster, foreignMachine, error: "tls_ca_changed");
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        KubernetesClusterSummaryDto summary = result.Data!.Single();
        await Assert.That(summary.Id).IsEqualTo(ownCluster);
        await Assert.That(summary.ReportingState).IsEqualTo("off");
        await Assert.That(summary.ReporterError).IsNull();
        await Assert.That(summary.ActiveReporterMachineId).IsNull();
        string body = JsonSerializer.Serialize(result.Data);
        await Assert.That(body.Contains("foreign-secret-host", StringComparison.Ordinal)).IsFalse();
        await Assert.That(body.Contains("v9.99.9", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task ListAsync_HolderWithoutACandidateRow_IsStillNamed()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long holder = await SeedMachineAsync(dbFactory.Context, tenantId, "former-candidate");
        long other = await SeedMachineAsync(dbFactory.Context, tenantId, "other-candidate");
        long clusterId = await SeedReportingClusterAsync(dbFactory.Context, tenantId, FingerprintA, holder, Now);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, other);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        await Assert.That(result.Data![0].ActiveReporterMachineName).IsEqualTo("former-candidate");
    }

    [Test]
    public async Task ListAsync_AgentlessControlPlaneRole_NamesTheCluster()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedReportingClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedReadyNodeAsync(dbFactory.Context, tenantId, clusterId, "a-worker", "True", roles: "[\"worker\"]");
        await SeedReadyNodeAsync(dbFactory.Context, tenantId, clusterId, "z-cp", "True", roles: "[\"control-plane\",\"etcd\"]");
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        await Assert.That(result.Data![0].Name).IsEqualTo("k3s z-cp");
    }

    [Test]
    public async Task ListAsync_ManyClusters_ReadsNodesCandidatesAndNamesOncePerRequest()
    {
        // Intent: the list must cost a fixed number of reads however many clusters there are.
        using TestDatabaseFactory dbFactory = new();
        IKubernetesInventoryRepository repo = Substitute.For<IKubernetesInventoryRepository>();
        repo.ListClustersAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new KubernetesCluster { Id = 1, TenantId = 1, CaFingerprint = FingerprintA, Distro = "k3s", FirstSeenAt = T0, LastSeenAt = T0, ActiveReporterMachineId = 7 },
            new KubernetesCluster { Id = 2, TenantId = 1, CaFingerprint = FingerprintB, Distro = "k3s", FirstSeenAt = T0, LastSeenAt = T0 },
            new KubernetesCluster { Id = 3, TenantId = 1, CaFingerprint = "c" + FingerprintA[1..], Distro = "k3s", FirstSeenAt = T0, LastSeenAt = T0 },
        ]);
        repo.ListNodesAsync(1, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.ListCandidatesAsync(1, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>()).Returns(
            [new KubernetesReporterCandidate { TenantId = 1, ClusterId = 2, MachineId = 8, LastReportAt = T0 }]);
        repo.GetMachineNamesInTenantAsync(1, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<long, string>)new Dictionary<long, string> { [7] = "holder", [8] = "candidate" });
        KubernetesClusterHandler handler = CreateHandler(dbFactory, kubernetesRepo: repo);

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await handler.ListAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.Count).IsEqualTo(3);
        await repo.Received(1).ListNodesAsync(1, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>());
        await repo.Received(1).ListCandidatesAsync(1, Arg.Is<IReadOnlyCollection<long>>(ids => ids.Count == 3), Arg.Any<CancellationToken>());
        await repo.Received(1).GetMachineNamesInTenantAsync(
            1, Arg.Is<IReadOnlyCollection<long>>(ids => ids.OrderBy(i => i).SequenceEqual(new long[] { 7, 8 })), Arg.Any<CancellationToken>());
    }

    // ========== GetAsync: reporting ==========

    [Test]
    public async Task GetAsync_ReportingCluster_ReturnsCandidatesEligibleMachinesAndTheUid()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long activeReporter = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-host");
        long standby = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-standby");
        long controller = await SeedMachineAsync(dbFactory.Context, tenantId, "k0s-controller");
        long clusterId = await SeedReportingClusterAsync(
            dbFactory.Context, tenantId, FingerprintA, activeReporter, Now.AddMinutes(-3), uid: "3f0c2d9e-aaaa-4bbb-8ccc-0123456789ab");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", activeReporter, controlPlane: true);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, activeReporter, lastReport: Now.AddMinutes(-2));
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, standby, error: "tls_ca_changed");
        await SeedLocalReportAsync(dbFactory.Context, tenantId, activeReporter, LocalReportJson(FingerprintA, enrolled: true));
        await SeedLocalReportAsync(dbFactory.Context, tenantId, standby, LocalReportJson(FingerprintA, enrolled: true));
        await SeedLocalReportAsync(dbFactory.Context, tenantId, controller, LocalReportJson(FingerprintA));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(activeReporter, tenantId, healthStatus: 0));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(standby, tenantId, healthStatus: 3));
        InMemoryMachinePingService ping = new();
        await ping.SetAgentCapabilitiesAsync(activeReporter, 1);
        KubernetesClusterHandler handler = CreateHandler(dbFactory, pingService: ping);

        ServiceResult<KubernetesClusterDetailDto> result = await handler.GetAsync(clusterId, tenantId, CancellationToken.None);

        KubernetesClusterDetailDto detail = result.Data!;
        await Assert.That(detail.KubeSystemUid).IsEqualTo("3f0c2d9e-aaaa-4bbb-8ccc-0123456789ab");
        await Assert.That(detail.ReportingState).IsEqualTo("error");
        await Assert.That(detail.ReporterError).IsEqualTo("tls_ca_changed");
        await Assert.That(detail.Candidates.Count).IsEqualTo(2);
        KubernetesCandidateDto activeReporterCandidate = detail.Candidates.Single(c => c.MachineId == activeReporter);
        await Assert.That(activeReporterCandidate.IsActiveReporter).IsTrue();
        await Assert.That(activeReporterCandidate.MachineName).IsEqualTo("cp-host");
        await Assert.That(activeReporterCandidate.Online).IsTrue();
        await Assert.That(activeReporterCandidate.LastReportAt).IsEqualTo(Now.AddMinutes(-2));
        KubernetesCandidateDto standbyCandidate = detail.Candidates.Single(c => c.MachineId == standby);
        await Assert.That(standbyCandidate.IsActiveReporter).IsFalse();
        await Assert.That(standbyCandidate.ReporterError).IsEqualTo("tls_ca_changed");
        await Assert.That(standbyCandidate.Online).IsFalse();

        await Assert.That(detail.EligibleMachines.Select(m => m.MachineId).ToArray()).IsEquivalentTo(new[] { controller, activeReporter, standby });
        KubernetesEligibleMachineDto activeReporterEligible = detail.EligibleMachines.Single(m => m.MachineId == activeReporter);
        await Assert.That(activeReporterEligible.Enrolled).IsTrue();
        await Assert.That(activeReporterEligible.RemoteCommandsEnabled).IsTrue();
        await Assert.That(activeReporterEligible.IsPaused).IsFalse();
        KubernetesEligibleMachineDto controllerEligible = detail.EligibleMachines.Single(m => m.MachineId == controller);
        await Assert.That(controllerEligible.Enrolled).IsFalse();
        await Assert.That(controllerEligible.RemoteCommandsEnabled).IsFalse();
        await Assert.That(controllerEligible.Online).IsFalse();
    }

    [Test]
    public async Task GetAsync_EligibleMachines_NeverIncludeAnotherTenantsMachinesHoldingTheSameCa()
    {
        // Intent: eligibility is found through the CA fingerprint, which two tenants can share, so the
        // tenant boundary must hold: the other tenant's control-plane host is never offered.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long own = await SeedMachineAsync(dbFactory.Context, tenantId, "own-cp");
        long foreign = await SeedMachineAsync(dbFactory.Context, otherTenantId, "foreign-cp");
        long clusterId = await SeedReportingClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedLocalReportAsync(dbFactory.Context, tenantId, own, LocalReportJson(FingerprintA));
        await SeedLocalReportAsync(dbFactory.Context, otherTenantId, foreign, LocalReportJson(FingerprintA));
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterDetailDto> result = await handler.GetAsync(clusterId, tenantId, CancellationToken.None);

        await Assert.That(result.Data!.EligibleMachines.Select(m => m.MachineId).ToArray()).IsEquivalentTo(new[] { own });
    }

    [Test]
    public async Task GetAsync_EligibleMachines_ListTheSigningKeysAuthorizedOnEach()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildUser());
        long machine = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-host");
        long bare = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-bare");
        long clusterId = await SeedReportingClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedLocalReportAsync(dbFactory.Context, tenantId, machine, LocalReportJson(FingerprintA));
        await SeedLocalReportAsync(dbFactory.Context, tenantId, bare, LocalReportJson(FingerprintA));
        int keyId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId));
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildMachineAuthorizedKey(machineId: machine, signingKeyId: keyId, tenantId: tenantId, authorizedByUserId: userId));
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterDetailDto> result = await handler.GetAsync(clusterId, tenantId, CancellationToken.None);

        await Assert.That(result.Data!.EligibleMachines.Single(m => m.MachineId == machine).AuthorizedSigningKeyIds.ToArray())
            .IsEquivalentTo(new[] { keyId });
        await Assert.That(result.Data.EligibleMachines.Single(m => m.MachineId == bare).AuthorizedSigningKeyIds.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetAsync_ClusterWithNothingReporting_HasEmptyCandidateAndEligibleLists()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterDetailDto> result = await handler.GetAsync(clusterId, tenantId, CancellationToken.None);

        await Assert.That(result.Data!.Candidates.Count).IsEqualTo(0);
        await Assert.That(result.Data.EligibleMachines.Count).IsEqualTo(0);
        await Assert.That(result.Data.KubeSystemUid).IsNull();
    }

    [Test]
    public async Task GetAsync_NodeReporterFields_AreMappedOntoTheNodeViews()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedReportingClusterAsync(dbFactory.Context, tenantId, FingerprintA, version: "v1.34.1+k3s1");
        await dbFactory.Context.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = "worker-1",
            KubeletVersion = "v1.30.4+k3s1",
            Roles = "[\"worker\"]",
            ReadyStatus = "False",
            ReadySince = T0.AddMinutes(20),
            Conditions = "[{\"type\":\"DiskPressure\",\"status\":\"True\",\"lastTransitionTime\":\"2026-10-11T12:15:00Z\"}]",
            Unschedulable = true,
            UnschedulableSince = T0.AddMinutes(25),
            ContainerRuntimeVersion = "containerd://1.7.23",
            PodCount = 12,
            PodCapacity = 110,
            LastReporterSeenAt = T0.AddMinutes(28),
        });
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterDetailDto> result = await handler.GetAsync(clusterId, tenantId, CancellationToken.None);

        KubernetesNodeDto node = result.Data!.Nodes.Single();
        await Assert.That(node.Roles.ToArray()).IsEquivalentTo(new[] { "worker" });
        await Assert.That(node.ReadyStatus).IsEqualTo("False");
        await Assert.That(node.ReadySince).IsEqualTo(T0.AddMinutes(20));
        await Assert.That(node.Conditions.Single().Type).IsEqualTo("DiskPressure");
        await Assert.That(node.Unschedulable).IsTrue();
        await Assert.That(node.UnschedulableSince).IsEqualTo(T0.AddMinutes(25));
        await Assert.That(node.ContainerRuntimeVersion).IsEqualTo("containerd://1.7.23");
        await Assert.That(node.PodCount).IsEqualTo(12);
        await Assert.That(node.PodCapacity).IsEqualTo(110);
        await Assert.That(node.LastReporterSeenAt).IsEqualTo(T0.AddMinutes(28));
        await Assert.That(node.KubeletSkew).IsTrue();
    }

    [Test]
    public async Task GetAsync_TwoClustersSharingACa_OfferEachMachineOnlyToTheClusterItIsLinkedTo()
    {
        // Intent: two clusters of one tenant that share a CA fingerprint match every control-plane host
        // of either by fingerprint alone. A host already a node of one cluster, or a candidate of the
        // other, must be offered to that cluster only, and a host linked to neither to both.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long nodeOfFirst = await SeedMachineAsync(dbFactory.Context, tenantId, "first-cp");
        long candidateOfSecond = await SeedMachineAsync(dbFactory.Context, tenantId, "second-cp");
        long unlinked = await SeedMachineAsync(dbFactory.Context, tenantId, "unlinked-cp");
        long first = await SeedReportingClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        long second = await SeedReportingClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedNodeAsync(dbFactory.Context, tenantId, first, "first-1", nodeOfFirst, controlPlane: true);
        await SeedCandidateAsync(dbFactory.Context, tenantId, second, candidateOfSecond);
        foreach (long machine in new[] { nodeOfFirst, candidateOfSecond, unlinked })
        {
            await SeedLocalReportAsync(dbFactory.Context, tenantId, machine, LocalReportJson(FingerprintA));
        }

        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        KubernetesClusterDetailDto firstDetail = (await handler.GetAsync(first, tenantId, CancellationToken.None)).Data!;
        KubernetesClusterDetailDto secondDetail = (await handler.GetAsync(second, tenantId, CancellationToken.None)).Data!;

        await Assert.That(firstDetail.EligibleMachines.Select(m => m.MachineId).ToArray()).IsEquivalentTo(new[] { nodeOfFirst, unlinked });
        await Assert.That(secondDetail.EligibleMachines.Select(m => m.MachineId).ToArray()).IsEquivalentTo(new[] { candidateOfSecond, unlinked });
    }

    [Test]
    public async Task GetAsync_DeletedMachineWithAReportAndANodeRow_IsNotEligible()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        Machine deleted = TestDataBuilder.BuildMachine(tenantId, hostname: "deleted-cp");
        deleted.IsDeleted = true;
        long deletedId = await dbFactory.Context.InsertWithInt64IdentityAsync(deleted);
        long live = await SeedMachineAsync(dbFactory.Context, tenantId, "live-cp");
        long clusterId = await SeedReportingClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "deleted-1", deletedId, controlPlane: true);
        await SeedLocalReportAsync(dbFactory.Context, tenantId, deletedId, LocalReportJson(FingerprintA));
        await SeedLocalReportAsync(dbFactory.Context, tenantId, live, LocalReportJson(FingerprintA));
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterDetailDto> result = await handler.GetAsync(clusterId, tenantId, CancellationToken.None);

        await Assert.That(result.Data!.EligibleMachines.Select(m => m.MachineId).ToArray()).IsEquivalentTo(new[] { live });
    }

    [Test]
    public async Task GetAsync_CapabilityStoreUnavailable_StillReturnsTheClusterWithRemoteCommandsUnknown()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long machine = await SeedMachineAsync(dbFactory.Context, tenantId, "cp-host");
        long clusterId = await SeedReportingClusterAsync(dbFactory.Context, tenantId, FingerprintA, machine, Now);
        await SeedCandidateAsync(dbFactory.Context, tenantId, clusterId, machine);
        await SeedLocalReportAsync(dbFactory.Context, tenantId, machine, LocalReportJson(FingerprintA, enrolled: true));
        IMachinePingService ping = Substitute.For<IMachinePingService>();
        ping.GetAgentCapabilitiesBatchAsync(Arg.Any<IEnumerable<long>>())
            .Returns<Task<Dictionary<long, ulong>>>(_ => throw new InvalidOperationException("redis unavailable"));
        KubernetesClusterHandler handler = CreateHandler(dbFactory, pingService: ping);

        ServiceResult<KubernetesClusterDetailDto> result = await handler.GetAsync(clusterId, tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Candidates.Single().MachineId).IsEqualTo(machine);
        KubernetesEligibleMachineDto eligible = result.Data.EligibleMachines.Single();
        await Assert.That(eligible.MachineId).IsEqualTo(machine);
        await Assert.That(eligible.RemoteCommandsEnabled).IsNull();
        await Assert.That(eligible.Enrolled).IsTrue();
    }

    // ========== Constructor ==========

    [Test]
    public async Task Constructor_NullDependency_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = new(dbFactory.Context, NullLogger<DatabaseRepository>.Instance);
        FakeTimeProvider clock = new(Now);
        KubernetesReporterMachineReader machines = new(
            repo, repo, repo, repo, new InMemoryMachinePingService(), TestQuotaPause.Nothing(),
            TestMetricsFactory.CreateQuotaPauseMetrics(), TestMetricsFactory.CreateKubernetesInventoryMetrics(), clock,
            NullLogger<KubernetesReporterMachineReader>.Instance);

        await Assert.That(() => new KubernetesClusterHandler(null!, machines, repo, repo, clock)).Throws<ArgumentNullException>();
        await Assert.That(() => new KubernetesClusterHandler(repo, null!, repo, repo, clock)).Throws<ArgumentNullException>();
        await Assert.That(() => new KubernetesClusterHandler(repo, machines, null!, repo, clock)).Throws<ArgumentNullException>();
        await Assert.That(() => new KubernetesClusterHandler(repo, machines, repo, null!, clock)).Throws<ArgumentNullException>();
        await Assert.That(() => new KubernetesClusterHandler(repo, machines, repo, repo, null!)).Throws<ArgumentNullException>();
    }

    // ========== RenameAsync ==========

    [Test]
    public async Task RenameAsync_SetsTheDisplayNameAndReturnsTheUpdatedSummary()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", controlPlane: true);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterSummaryDto> result = await handler.RenameAsync(clusterId, tenantId, 7, "Production", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Name).IsEqualTo("Production");
        await Assert.That(result.Data.DisplayName).IsEqualTo("Production");
        await Assert.That(result.Data.NodeCount).IsEqualTo(1);
        KubernetesCluster stored = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
        await Assert.That(stored.DisplayName).IsEqualTo("Production");
    }

    [Test]
    public async Task RenameAsync_TrimsSurroundingWhitespace()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterSummaryDto> result = await handler.RenameAsync(clusterId, tenantId, 7, "   Staging  ", CancellationToken.None);

        await Assert.That(result.Data!.DisplayName).IsEqualTo("Staging");
        KubernetesCluster stored = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
        await Assert.That(stored.DisplayName).IsEqualTo("Staging");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task RenameAsync_EmptyOrWhitespace_ResetsToTheDerivedName(string displayName)
    {
        await AssertResetToDerivedNameAsync(displayName);
    }

    [Test]
    public async Task RenameAsync_NullName_ResetsToTheDerivedName()
    {
        await AssertResetToDerivedNameAsync(null);
    }

    private static async Task AssertResetToDerivedNameAsync(string? displayName)
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "Production");
        await SeedNodeAsync(dbFactory.Context, tenantId, clusterId, "cp-1", controlPlane: true);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterSummaryDto> result = await handler.RenameAsync(clusterId, tenantId, 7, displayName, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.DisplayName).IsNull();
        await Assert.That(result.Data.Name).IsEqualTo("k3s cp-1");
        KubernetesCluster stored = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
        await Assert.That(stored.DisplayName).IsNull();
    }

    [Test]
    public async Task RenameAsync_NameOfExactlyOneHundredCharacters_Succeeds()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);
        string name = new('x', 100);

        ServiceResult<KubernetesClusterSummaryDto> result = await handler.RenameAsync(clusterId, tenantId, 7, name, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.DisplayName).IsEqualTo(name);
    }

    [Test]
    public async Task RenameAsync_NameLongerThanOneHundredCharactersAfterTrimming_IsRejectedWithoutWriting()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "Production");
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterSummaryDto> result = await handler.RenameAsync(clusterId, tenantId, 7, new string('x', 101), CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsNotNull();
        KubernetesCluster stored = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
        await Assert.That(stored.DisplayName).IsEqualTo("Production");
        int auditRows = await dbFactory.Context.AuditLog.CountAsync(a => a.Action == AuditAction.KubernetesClusterRenamed);
        await Assert.That(auditRows).IsEqualTo(0);
    }

    [Test]
    public async Task RenameAsync_WritesOneAuditRowWithTheRenamedValue()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA);
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        await handler.RenameAsync(clusterId, tenantId, 7, "  Production ", CancellationToken.None);

        List<AuditLogEntry> rows = await dbFactory.Context.AuditLog.ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(1);
        AuditLogEntry row = rows[0];
        await Assert.That(row.Action).IsEqualTo(AuditAction.KubernetesClusterRenamed);
        await Assert.That(row.ResourceType).IsEqualTo(AuditResourceType.KubernetesCluster);
        await Assert.That(row.ResourceId).IsEqualTo(clusterId.ToString());
        await Assert.That(row.TenantId).IsEqualTo(tenantId);
        await Assert.That(row.UserId).IsEqualTo(7);
        await Assert.That(row.MachineId).IsNull();
        using JsonDocument details = JsonDocument.Parse(row.Details!);
        await Assert.That(details.RootElement.GetProperty("displayName").GetString()).IsEqualTo("Production");
        bool hasPrevious = details.RootElement.TryGetProperty("previousDisplayName", out JsonElement previous) && (previous.ValueKind != JsonValueKind.Null);
        await Assert.That(hasPrevious).IsFalse();
    }

    [Test]
    public async Task RenameAsync_RenamingANamedCluster_AuditsThePreviousAndTheNewName()
    {
        // Intent: the audit trail says what the name was as well as what it became, so a rename can be
        // traced and undone from the log.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "Production");
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        await handler.RenameAsync(clusterId, tenantId, 7, "Staging", CancellationToken.None);

        AuditLogEntry row = await dbFactory.Context.AuditLog.SingleAsync();
        using JsonDocument details = JsonDocument.Parse(row.Details!);
        await Assert.That(details.RootElement.GetProperty("displayName").GetString()).IsEqualTo("Staging");
        await Assert.That(details.RootElement.GetProperty("previousDisplayName").GetString()).IsEqualTo("Production");
    }

    [Test]
    public async Task RenameAsync_Reset_AuditsThePreviousNameAndNoNewName()
    {
        // Intent: a reset to the derived name is recorded with the name it removed. The audit serializer
        // leaves out null values, so without the previous name a reset would be an empty record.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "Production");
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        await handler.RenameAsync(clusterId, tenantId, 7, "   ", CancellationToken.None);

        AuditLogEntry row = await dbFactory.Context.AuditLog.SingleAsync();
        await Assert.That(row.Action).IsEqualTo(AuditAction.KubernetesClusterRenamed);
        await Assert.That(row.ResourceId).IsEqualTo(clusterId.ToString());
        using JsonDocument details = JsonDocument.Parse(row.Details!);
        bool hasName = details.RootElement.TryGetProperty("displayName", out JsonElement name) && (name.ValueKind != JsonValueKind.Null);
        await Assert.That(hasName).IsFalse();
        await Assert.That(details.RootElement.GetProperty("previousDisplayName").GetString()).IsEqualTo("Production");
    }

    [Test]
    public async Task RenameAsync_AuditsInTheSameTransactionBeforeItCommits()
    {
        // Intent: the audit entry and the rename commit or roll back together, so the audit insert has
        // to happen after the rename and before the commit. The previous name is read after the tenant
        // lock is taken and before the rename, inside the transaction, so it is the name the rename
        // replaces and no concurrent rename can slip in between. The cluster is read again after the
        // commit to build the response.
        List<string> calls = [];
        IKubernetesInventoryRepository kubernetesRepo = Substitute.For<IKubernetesInventoryRepository>();
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction transaction = Substitute.For<IDatabaseTransaction>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("begin");

            return transaction;
        });
        kubernetesRepo.AcquireKubernetesTenantLockAsync(5, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("lock");

            return Task.CompletedTask;
        });
        kubernetesRepo.GetClusterAsync(5, 11, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("read");

            return Task.FromResult<KubernetesCluster?>(new KubernetesCluster
            {
                Id = 11,
                TenantId = 5,
                CaFingerprint = FingerprintA,
                Distro = "k3s",
                DisplayName = "Production",
                FirstSeenAt = T0,
                LastSeenAt = T0,
            });
        });
        kubernetesRepo.RenameClusterAsync(5, 11, "Production", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("rename");

            return Task.FromResult(true);
        });
        auditLog.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("audit");

            return Task.CompletedTask;
        });
        transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("commit");

            return Task.CompletedTask;
        });
        kubernetesRepo.ListNodesAsync(5, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>()).Returns([]);
        kubernetesRepo.ListCandidatesAsync(5, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>()).Returns([]);
        kubernetesRepo.GetMachineNamesInTenantAsync(5, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<long, string>)new Dictionary<long, string>());
        KubernetesClusterHandler handler = new(kubernetesRepo, SubstituteReader(kubernetesRepo), transactionProvider, auditLog, new FakeTimeProvider(Now));

        ServiceResult<KubernetesClusterSummaryDto> result = await handler.RenameAsync(11, 5, 7, "Production", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(string.Join(" > ", calls)).IsEqualTo("begin > lock > read > rename > audit > commit > read");
        await auditLog.Received(1).InsertAuditLogAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.KubernetesClusterRenamed), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RenameAsync_AuditInsertFails_LeavesTheNameUnchanged()
    {
        // Intent: a rename that cannot be audited must not stick.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        long clusterId = await SeedClusterAsync(dbFactory.Context, tenantId, FingerprintA, displayName: "Production");
        IAuditLogRepository failingAudit = Substitute.For<IAuditLogRepository>();
        failingAudit.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("audit store unavailable"));
        KubernetesClusterHandler handler = CreateHandler(dbFactory, failingAudit);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.RenameAsync(clusterId, tenantId, 7, "Renamed", CancellationToken.None));

        KubernetesCluster stored = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
        await Assert.That(stored.DisplayName).IsEqualTo("Production");
    }

    [Test]
    public async Task RenameAsync_UnknownCluster_ReturnsNotFoundWithoutAuditingOrCommitting()
    {
        IKubernetesInventoryRepository kubernetesRepo = Substitute.For<IKubernetesInventoryRepository>();
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction transaction = Substitute.For<IDatabaseTransaction>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        kubernetesRepo.GetClusterAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns((KubernetesCluster?)null);
        KubernetesClusterHandler handler = new(kubernetesRepo, SubstituteReader(kubernetesRepo), transactionProvider, auditLog, new FakeTimeProvider(Now));

        ServiceResult<KubernetesClusterSummaryDto> result = await handler.RenameAsync(11, 5, 7, "Production", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
        await kubernetesRepo.DidNotReceive().RenameClusterAsync(
            Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await auditLog.DidNotReceive().InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RenameAsync_AnotherTenantsCluster_ReturnsNotFoundAndLeavesItUntouched()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory.Context);
        int otherTenantId = await SeedTenantAsync(dbFactory.Context);
        long foreignClusterId = await SeedClusterAsync(dbFactory.Context, otherTenantId, FingerprintB, displayName: "Theirs");
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterSummaryDto> result = await handler.RenameAsync(foreignClusterId, tenantId, 7, "Mine", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
        KubernetesCluster stored = await dbFactory.Context.KubernetesClusters.SingleAsync(c => c.Id == foreignClusterId);
        await Assert.That(stored.DisplayName).IsEqualTo("Theirs");
        int auditRows = await dbFactory.Context.AuditLog.CountAsync();
        await Assert.That(auditRows).IsEqualTo(0);
    }

    [Test]
    public async Task RenameAsync_NullTenant_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        KubernetesClusterHandler handler = CreateHandler(dbFactory);

        ServiceResult<KubernetesClusterSummaryDto> result = await handler.RenameAsync(1, null, 7, "Production", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }
}
