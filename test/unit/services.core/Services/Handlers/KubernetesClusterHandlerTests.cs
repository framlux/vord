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
using Framlux.FleetManagement.Services.Core.Models.Kubernetes;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
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

    private static KubernetesClusterHandler CreateHandler(TestDatabaseFactory dbFactory, IAuditLogRepository? auditLog = null)
    {
        DatabaseRepository repo = new(dbFactory.Context, Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseRepository>.Instance);

        return new KubernetesClusterHandler(repo, repo, auditLog ?? repo);
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
        await Assert.That(summary.ReportingState).IsEqualTo("identity_only");
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
        await Assert.That(detail.ReportingState).IsEqualTo("identity_only");
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
        KubernetesClusterHandler handler = new(kubernetesRepo, transactionProvider, auditLog);

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
        KubernetesClusterHandler handler = new(kubernetesRepo, transactionProvider, auditLog);

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
