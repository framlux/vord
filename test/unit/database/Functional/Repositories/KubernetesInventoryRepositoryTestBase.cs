// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Seed and assertion helpers shared by the Kubernetes inventory repository test classes: the
/// fingerprints and clock they build on, the repository factory, and the rows a test seeds before it
/// applies a report.
/// </summary>
public abstract class KubernetesInventoryRepositoryTestBase
{
    protected const string FingerprintA = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";
    protected const string FingerprintB = "0f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a6978";
    protected const string FingerprintC = "c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00";

    protected static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    protected static IKubernetesInventoryRepository CreateRepo(
        TestDatabaseFactory dbFactory, ILogger<Database.Repositories.DatabaseRepository>? logger = null)
    {
        return new Database.Repositories.DatabaseRepository(
            dbFactory.Context, logger ?? NullLogger<Database.Repositories.DatabaseRepository>.Instance);
    }

    protected static async Task<int> SeedTenantAsync(DatabaseContext db)
    {
        return await db.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant());
    }

    protected static async Task<long> SeedMachineAsync(DatabaseContext db, int tenantId, bool isDeleted = false, string? systemId = null)
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId);
        machine.IsDeleted = isDeleted;
        if (systemId != null)
        {
            machine.SystemId = systemId;
        }

        return await db.InsertWithInt64IdentityAsync(machine);
    }

    protected static async Task<long> SeedClusterAsync(
        DatabaseContext db, int tenantId, string fingerprint, DateTimeOffset seenAt, string distro = "k3s")
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = fingerprint,
            Distro = distro,
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
        });
    }

    protected static async Task<long> SeedNodeAsync(
        DatabaseContext db,
        int tenantId,
        long clusterId,
        string nodeName,
        long? machineId,
        string? reportedFingerprint,
        DateTimeOffset? lastLocalReportAt,
        bool mismatch = false)
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = nodeName,
            MachineId = machineId,
            ReportedCaFingerprint = reportedFingerprint,
            FingerprintMismatch = mismatch,
            LastLocalReportAt = lastLocalReportAt,
        });
    }

    protected static LocalNodeReport Report(
        long telemetryId,
        int tenantId,
        long machineId,
        DateTimeOffset at,
        string nodeName = "node-1",
        string? fingerprint = FingerprintA,
        bool hasKubelet = true,
        bool controlPlane = false,
        string distro = "k3s",
        string? kubeletVersion = "v1.30.4+k3s1",
        string nodeNameSource = "certificate",
        bool reporterEnrolled = false,
        string? reporterError = null)
    {
        return new LocalNodeReport(
            telemetryId, tenantId, machineId, at, distro, nodeName, hasKubelet, controlPlane, fingerprint, kubeletVersion,
            nodeNameSource, reporterEnrolled, reporterError);
    }

    protected static async Task SeedCandidateAsync(
        DatabaseContext db, int tenantId, long clusterId, long machineId, DateTimeOffset lastReportAt, string? reporterError = null)
    {
        await db.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            MachineId = machineId,
            ReporterError = reporterError,
            LastReportAt = lastReportAt,
        });
    }

    protected static async Task<long> SeedReporterNodeAsync(
        DatabaseContext db,
        int tenantId,
        long clusterId,
        string nodeName,
        string? nodeMachineId,
        DateTimeOffset? reporterSeenAt,
        long? machineId = null,
        DateTimeOffset? lastLocalReportAt = null)
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = nodeName,
            MachineId = machineId,
            LastLocalReportAt = lastLocalReportAt,
            ReportedCaFingerprint = lastLocalReportAt == null ? null : FingerprintA,
            KubeletVersion = lastLocalReportAt == null ? null : "v1.30.4+k3s1",
            LocalControlPlane = lastLocalReportAt != null,
            NodeMachineId = nodeMachineId,
            Roles = "[\"control-plane\"]",
            ReadyStatus = "True",
            ReadySince = reporterSeenAt,
            Conditions = "[{\"type\":\"Ready\",\"status\":\"True\"}]",
            Unschedulable = true,
            UnschedulableSince = reporterSeenAt,
            ContainerRuntimeVersion = "containerd://1.7.23",
            PodCount = 12,
            PodCapacity = 110,
            LastReporterSeenAt = reporterSeenAt,
        });
    }

    protected static async Task<long> SeedReportingClusterAsync(
        DatabaseContext db,
        int tenantId,
        string fingerprint,
        DateTimeOffset seenAt,
        DateTimeOffset? lastSnapshotReceivedAt,
        long? activeReporterMachineId = null)
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = fingerprint,
            DisplayName = "production",
            Distro = "k3s",
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
            KubeSystemUid = $"uid-{Guid.NewGuid():N}",
            ClusterVersion = "v1.30.4+k3s1",
            ReporterError = "api_unreachable",
            RoleOutdated = true,
            Truncated = true,
            LeaseTransitions = 7,
            ActiveReporterMachineId = activeReporterMachineId,
            LastSnapshotReceivedAt = lastSnapshotReceivedAt,
            LastSnapshotTelemetryId = 99,
            LeaseUid = "lease-uid",
            LeaseCreatedAt = seenAt,
        });
    }

    protected static async Task<KubernetesNode> NodeOfMachineAsync(DatabaseContext db, long machineId)
    {
        return await db.KubernetesNodes.SingleAsync(n => n.MachineId == machineId);
    }

    protected static async Task<KubernetesCluster> ClusterAsync(DatabaseContext db, long clusterId)
    {
        return await db.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
    }

    protected static async Task AssertNothingWrittenAsync(DatabaseContext db)
    {
        await Assert.That(await db.KubernetesClusters.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.KubernetesNodes.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.KubernetesReporterCandidates.CountAsync()).IsEqualTo(0);
    }
}
