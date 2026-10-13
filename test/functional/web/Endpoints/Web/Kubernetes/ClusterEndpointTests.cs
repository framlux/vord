// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web.Kubernetes;

/// <summary>
/// Functional tests for the Kubernetes cluster endpoints (<c>/api/v1/clusters</c>) and the
/// Kubernetes block on the machine detail response, through the full HTTP pipeline.
/// </summary>
public sealed class ClusterEndpointTests
{
    private const string FingerprintA = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";
    private const string FingerprintB = "0f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a6978";

    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static async Task<int> SeedTenantAsync(DatabaseContext db, string name)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        await db.InsertWithInt32IdentityAsync(new TenantSubscription
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        return tenant.Id;
    }

    private static async Task<int> SeedUserAsync(DatabaseContext db, int tenantId, UserAccountRoles role)
    {
        UserAccount user = new()
        {
            ExternalId = $"ext-k8s-{Guid.NewGuid():N}",
            Username = $"k8s-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        await db.InsertAsync(new UserTenantRole
        {
            UserId = user.Id,
            AssignedTenantId = tenantId,
            Role = role,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        return user.Id;
    }

    private static async Task<long> SeedMachineAsync(DatabaseContext db, int tenantId, string name)
    {
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = name,
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        };

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
            LastSeenAt = T0.AddMinutes(10),
        });
    }

    private static async Task<long> SeedNodeAsync(
        DatabaseContext db,
        int tenantId,
        long clusterId,
        string nodeName,
        long? machineId = null,
        bool controlPlane = false,
        string? kubeletVersion = "v1.30.4+k3s1",
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
            LastLocalReportAt = T0.AddMinutes(5),
        });
    }

    private static async Task SeedCandidateAsync(
        DatabaseContext db, int tenantId, long clusterId, long machineId, string? error = null, DateTimeOffset? lastReportAt = null)
    {
        await db.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            MachineId = machineId,
            ReporterError = error,
            LastReportAt = lastReportAt ?? T0.AddMinutes(20),
        });
    }

    private static async Task SeedLocalReportAsync(
        DatabaseContext db, int tenantId, long machineId, string fingerprint, bool controlPlane = true, bool adminCredential = true, bool enrolled = false)
    {
        await db.InsertAsync(new MachineTelemetry
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = 14,
            Payload = $$"""{"distro":"k3s","kubelet_version":"v1.30.4+k3s1","node_name":"cp-1","node_name_source":"certificate","has_kubelet":true,"control_plane":{{(controlPlane ? "true" : "false")}},"ca_fingerprint":"{{fingerprint}}","admin_credential_present":{{(adminCredential ? "true" : "false")}},"reporter_enrolled":{{(enrolled ? "true" : "false")}}}""",
            ReceivedAt = DateTimeOffset.UtcNow,
            ServerReceivedAt = DateTimeOffset.UtcNow,
            SourceEventId = Guid.NewGuid().ToString("N"),
        });
    }

    private static async Task<int> SeedSigningKeyForAsync(DatabaseContext db, int tenantId, int userId, long machineId)
    {
        int keyId = await db.InsertWithInt32IdentityAsync(new UserSigningKey
        {
            UserId = userId,
            TenantId = tenantId,
            Label = "Test Key",
            PublicKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            PublicKeyFingerprint = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.InsertWithInt32IdentityAsync(new MachineAuthorizedKey
        {
            MachineId = machineId,
            SigningKeyId = keyId,
            TenantId = tenantId,
            AuthorizedAt = DateTimeOffset.UtcNow,
            AuthorizedByUserId = userId,
        });

        return keyId;
    }

    private static HttpClient ClientFor(FunctionalTestFactory factory, int userId, int tenantId, UserAccountRoles role)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)role)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // ========== List ==========

    [Test]
    public async Task ClusterList_AsViewer_ReturnsCamelCaseSummaries()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long machineId = await SeedMachineAsync(db, tenantId, "cp-host");
        long clusterId = await SeedClusterAsync(db, tenantId, FingerprintA);
        await SeedNodeAsync(db, tenantId, clusterId, "cp-1", machineId, controlPlane: true);
        await SeedNodeAsync(db, tenantId, clusterId, "worker-1");
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/clusters");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsTrue();
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(1);
        JsonElement cluster = data[0];
        await Assert.That(cluster.GetProperty("id").GetInt64()).IsEqualTo(clusterId);
        await Assert.That(cluster.GetProperty("name").GetString()).IsEqualTo("k3s cp-1");
        await Assert.That(cluster.GetProperty("displayName").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(cluster.GetProperty("distro").GetString()).IsEqualTo("k3s");
        await Assert.That(cluster.GetProperty("nodeCount").GetInt32()).IsEqualTo(2);
        await Assert.That(cluster.GetProperty("managedNodeCount").GetInt32()).IsEqualTo(1);
        await Assert.That(cluster.GetProperty("unmanagedNodeCount").GetInt32()).IsEqualTo(1);
        await Assert.That(cluster.GetProperty("kubeletVersions")[0].GetString()).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(cluster.GetProperty("reportingState").GetString()).IsEqualTo("off");
        await Assert.That(cluster.GetProperty("activeReporterMachineId").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(cluster.GetProperty("activeReporterMachineName").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(cluster.GetProperty("clusterVersion").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(cluster.GetProperty("reporterError").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(cluster.GetProperty("truncated").GetBoolean()).IsFalse();
        await Assert.That(cluster.GetProperty("lastSnapshotReceivedAt").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(cluster.GetProperty("staleSince").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(cluster.GetProperty("readyCount").GetInt32()).IsEqualTo(0);
        await Assert.That(cluster.GetProperty("notReadyCount").GetInt32()).IsEqualTo(0);
        await Assert.That(cluster.GetProperty("unknownCount").GetInt32()).IsEqualTo(2);
        await Assert.That(cluster.GetProperty("firstSeenAt").GetDateTimeOffset()).IsEqualTo(T0);
        await Assert.That(cluster.GetProperty("lastSeenAt").GetDateTimeOffset()).IsEqualTo(T0.AddMinutes(10));
    }

    [Test]
    public async Task ClusterList_NoClusters_ReturnsEmptyArray()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/clusters");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(doc.RootElement.GetProperty("data").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task ClusterList_TwoTenantsSameFingerprint_EachSeesOnlyOwn()
    {
        // Intent: two tenants that report the same CA get separate clusters, and neither list shows,
        // counts or names anything from the other.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenant1Id = await SeedTenantAsync(db, "Tenant 1");
        int tenant2Id = await SeedTenantAsync(db, "Tenant 2");
        int user1Id = await SeedUserAsync(db, tenant1Id, UserAccountRoles.Viewer);
        int user2Id = await SeedUserAsync(db, tenant2Id, UserAccountRoles.Viewer);
        long cluster1Id = await SeedClusterAsync(db, tenant1Id, FingerprintA);
        long cluster2Id = await SeedClusterAsync(db, tenant2Id, FingerprintA);
        await SeedNodeAsync(db, tenant1Id, cluster1Id, "tenant1-cp", controlPlane: true);
        await SeedNodeAsync(db, tenant2Id, cluster2Id, "tenant2-cp", controlPlane: true);
        await SeedNodeAsync(db, tenant2Id, cluster2Id, "tenant2-worker");

        HttpResponseMessage response1 = await ClientFor(factory, user1Id, tenant1Id, UserAccountRoles.Viewer).GetAsync("/api/v1/clusters");
        HttpResponseMessage response2 = await ClientFor(factory, user2Id, tenant2Id, UserAccountRoles.Viewer).GetAsync("/api/v1/clusters");

        string body1 = await response1.Content.ReadAsStringAsync();
        string body2 = await response2.Content.ReadAsStringAsync();
        using JsonDocument doc1 = JsonDocument.Parse(body1);
        using JsonDocument doc2 = JsonDocument.Parse(body2);
        JsonElement data1 = doc1.RootElement.GetProperty("data");
        JsonElement data2 = doc2.RootElement.GetProperty("data");
        await Assert.That(data1.GetArrayLength()).IsEqualTo(1);
        await Assert.That(data1[0].GetProperty("id").GetInt64()).IsEqualTo(cluster1Id);
        await Assert.That(data1[0].GetProperty("nodeCount").GetInt32()).IsEqualTo(1);
        await Assert.That(data1[0].GetProperty("name").GetString()).IsEqualTo("k3s tenant1-cp");
        await Assert.That(data2.GetArrayLength()).IsEqualTo(1);
        await Assert.That(data2[0].GetProperty("id").GetInt64()).IsEqualTo(cluster2Id);
        await Assert.That(data2[0].GetProperty("nodeCount").GetInt32()).IsEqualTo(2);
        await Assert.That(body1.Contains("tenant2", StringComparison.Ordinal)).IsFalse();
        await Assert.That(body2.Contains("tenant1", StringComparison.Ordinal)).IsFalse();
    }

    // ========== Detail ==========

    [Test]
    public async Task ClusterDetail_AsViewer_ReturnsFingerprintAndNodesByName()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long machineId = await SeedMachineAsync(db, tenantId, "cp-host");
        long clusterId = await SeedClusterAsync(db, tenantId, FingerprintA, displayName: "Production");
        await SeedNodeAsync(db, tenantId, clusterId, "worker-1", mismatch: true);
        long cpNodeId = await SeedNodeAsync(db, tenantId, clusterId, "cp-1", machineId, controlPlane: true);
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/clusters/{clusterId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("id").GetInt64()).IsEqualTo(clusterId);
        await Assert.That(data.GetProperty("name").GetString()).IsEqualTo("Production");
        await Assert.That(data.GetProperty("displayName").GetString()).IsEqualTo("Production");
        await Assert.That(data.GetProperty("caFingerprint").GetString()).IsEqualTo(FingerprintA);
        await Assert.That(data.GetProperty("hasFingerprintMismatch").GetBoolean()).IsTrue();
        JsonElement nodes = data.GetProperty("nodes");
        await Assert.That(nodes.GetArrayLength()).IsEqualTo(2);
        await Assert.That(nodes[0].GetProperty("nodeName").GetString()).IsEqualTo("cp-1");
        await Assert.That(nodes[0].GetProperty("id").GetInt64()).IsEqualTo(cpNodeId);
        await Assert.That(nodes[0].GetProperty("controlPlane").GetBoolean()).IsTrue();
        await Assert.That(nodes[0].GetProperty("kubeletVersion").GetString()).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(nodes[0].GetProperty("machineId").GetInt64()).IsEqualTo(machineId);
        await Assert.That(nodes[0].GetProperty("machineName").GetString()).IsEqualTo("cp-host");
        await Assert.That(nodes[0].GetProperty("fingerprintMismatch").GetBoolean()).IsFalse();
        await Assert.That(nodes[0].GetProperty("lastLocalReportAt").GetDateTimeOffset()).IsEqualTo(T0.AddMinutes(5));
        await Assert.That(nodes[1].GetProperty("nodeName").GetString()).IsEqualTo("worker-1");
        await Assert.That(nodes[1].GetProperty("machineId").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(nodes[1].GetProperty("machineName").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(nodes[1].GetProperty("fingerprintMismatch").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task ClusterDetail_UnknownId_Returns404WithMessage()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/clusters/987654");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("message").GetString()).IsEqualTo("Cluster not found");
    }

    // ========== List: reporting ==========

    [Test]
    public async Task ClusterList_ReportingClusters_ReturnStateHolderVersionAndNodeCounts()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long activeReporterId = await SeedMachineAsync(db, tenantId, "cp-host");
        long standbyId = await SeedMachineAsync(db, tenantId, "cp-standby");
        DateTimeOffset snapshot = DateTimeOffset.UtcNow.AddMinutes(-5);
        long activeId = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = FingerprintA,
            Distro = "k3s",
            FirstSeenAt = T0,
            LastSeenAt = T0,
            ClusterVersion = "v1.34.1+k3s1",
            Truncated = true,
            ActiveReporterMachineId = activeReporterId,
            LastSnapshotReceivedAt = snapshot,
        });
        DateTimeOffset staleSnapshot = DateTimeOffset.UtcNow.AddHours(-5);
        long staleId = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = FingerprintB,
            Distro = "rke2",
            FirstSeenAt = T0,
            LastSeenAt = T0,
            ActiveReporterMachineId = standbyId,
            LastSnapshotReceivedAt = staleSnapshot,
            ReporterError = "credentials_rejected",
        });
        await SeedCandidateAsync(db, tenantId, activeId, activeReporterId);
        await SeedCandidateAsync(db, tenantId, staleId, standbyId);
        foreach ((string name, string status) in new[] { ("n1", "True"), ("n2", "True"), ("n3", "False"), ("n4", "Unknown") })
        {
            await db.InsertWithInt64IdentityAsync(new KubernetesNode
            {
                TenantId = tenantId,
                ClusterId = activeId,
                NodeName = name,
                ReadyStatus = status,
            });
        }

        HttpResponseMessage response = await ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer).GetAsync("/api/v1/clusters");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement data = doc.RootElement.GetProperty("data");
        JsonElement active = data.EnumerateArray().Single(c => c.GetProperty("id").GetInt64() == activeId);
        await Assert.That(active.GetProperty("reportingState").GetString()).IsEqualTo("active");
        await Assert.That(active.GetProperty("activeReporterMachineId").GetInt64()).IsEqualTo(activeReporterId);
        await Assert.That(active.GetProperty("activeReporterMachineName").GetString()).IsEqualTo("cp-host");
        await Assert.That(active.GetProperty("clusterVersion").GetString()).IsEqualTo("v1.34.1+k3s1");
        await Assert.That(active.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(active.GetProperty("lastSnapshotReceivedAt").GetDateTimeOffset()).IsEqualTo(snapshot);
        await Assert.That(active.GetProperty("staleSince").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(active.GetProperty("readyCount").GetInt32()).IsEqualTo(2);
        await Assert.That(active.GetProperty("notReadyCount").GetInt32()).IsEqualTo(1);
        await Assert.That(active.GetProperty("unknownCount").GetInt32()).IsEqualTo(1);
        JsonElement errored = data.EnumerateArray().Single(c => c.GetProperty("id").GetInt64() == staleId);
        await Assert.That(errored.GetProperty("reportingState").GetString()).IsEqualTo("error");
        await Assert.That(errored.GetProperty("reporterError").GetString()).IsEqualTo("credentials_rejected");
        await Assert.That(errored.GetProperty("staleSince").GetDateTimeOffset()).IsEqualTo(staleSnapshot);
    }

    [Test]
    public async Task ClusterList_OtherTenantsReportingClusterSharingTheCa_NeverShowsThroughNorChangesTheState()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenant1Id = await SeedTenantAsync(db, "Tenant 1");
        int tenant2Id = await SeedTenantAsync(db, "Tenant 2");
        int user1Id = await SeedUserAsync(db, tenant1Id, UserAccountRoles.Viewer);
        long ownClusterId = await SeedClusterAsync(db, tenant1Id, FingerprintA);
        long foreignMachineId = await SeedMachineAsync(db, tenant2Id, "tenant2-secret-host");
        long foreignClusterId = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenant2Id,
            CaFingerprint = FingerprintA,
            Distro = "k3s",
            FirstSeenAt = T0,
            LastSeenAt = T0,
            ClusterVersion = "v9.99.9",
            ActiveReporterMachineId = foreignMachineId,
            LastSnapshotReceivedAt = DateTimeOffset.UtcNow,
            ReporterError = "tls_ca_changed",
        });
        await SeedCandidateAsync(db, tenant2Id, foreignClusterId, foreignMachineId, error: "tls_ca_changed");

        HttpResponseMessage response = await ClientFor(factory, user1Id, tenant1Id, UserAccountRoles.Viewer).GetAsync("/api/v1/clusters");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement cluster = doc.RootElement.GetProperty("data").EnumerateArray().Single();
        await Assert.That(cluster.GetProperty("id").GetInt64()).IsEqualTo(ownClusterId);
        await Assert.That(cluster.GetProperty("reportingState").GetString()).IsEqualTo("off");
        await Assert.That(body.Contains("tenant2-secret-host", StringComparison.Ordinal)).IsFalse();
        await Assert.That(body.Contains("v9.99.9", StringComparison.Ordinal)).IsFalse();
        await Assert.That(body.Contains("tls_ca_changed", StringComparison.Ordinal)).IsFalse();
    }

    // ========== Detail: reporting ==========

    [Test]
    public async Task ClusterDetail_ReportingCluster_ReturnsNodeViewsCandidatesEligibleMachinesAndTheUid()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long activeReporterId = await SeedMachineAsync(db, tenantId, "cp-host");
        long standbyId = await SeedMachineAsync(db, tenantId, "cp-standby");
        long controllerId = await SeedMachineAsync(db, tenantId, "k0s-controller");
        long workerId = await SeedMachineAsync(db, tenantId, "worker-host");
        DateTimeOffset snapshot = DateTimeOffset.UtcNow.AddMinutes(-3);
        long clusterId = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = FingerprintA,
            Distro = "k3s",
            FirstSeenAt = T0,
            LastSeenAt = T0,
            KubeSystemUid = "3f0c2d9e-aaaa-4bbb-8ccc-0123456789ab",
            ClusterVersion = "v1.34.1+k3s1",
            ActiveReporterMachineId = activeReporterId,
            LastSnapshotReceivedAt = snapshot,
        });
        await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = "cp-1",
            MachineId = activeReporterId,
            LocalControlPlane = true,
            KubeletVersion = "v1.34.1+k3s1",
            Roles = "[\"control-plane\",\"etcd\"]",
            ReadyStatus = "True",
            ReadySince = T0.AddDays(1),
            Conditions = "[]",
            ContainerRuntimeVersion = "containerd://1.7.23",
            PodCount = 14,
            PodCapacity = 110,
            LastReporterSeenAt = snapshot,
        });
        await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = "worker-1",
            MachineId = workerId,
            KubeletVersion = "v1.30.4+k3s1",
            Roles = "[\"worker\"]",
            ReadyStatus = "False",
            Conditions = "[{\"type\":\"MemoryPressure\",\"status\":\"True\",\"lastTransitionTime\":\"2026-10-12T10:00:00Z\"}]",
            Unschedulable = true,
            UnschedulableSince = T0.AddDays(1),
        });
        await SeedCandidateAsync(db, tenantId, clusterId, activeReporterId, lastReportAt: snapshot);
        await SeedCandidateAsync(db, tenantId, clusterId, standbyId, error: "tls_ca_changed");
        await SeedLocalReportAsync(db, tenantId, activeReporterId, FingerprintA, enrolled: true);
        await SeedLocalReportAsync(db, tenantId, standbyId, FingerprintA, enrolled: true);
        await SeedLocalReportAsync(db, tenantId, controllerId, FingerprintA);
        await SeedLocalReportAsync(db, tenantId, workerId, FingerprintA, controlPlane: false, adminCredential: false);
        int keyId = await SeedSigningKeyForAsync(db, tenantId, userId, activeReporterId);
        await factory.Services.GetRequiredService<IMachinePingService>().SetAgentCapabilitiesAsync(activeReporterId, 1);
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/clusters/{clusterId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("kubeSystemUid").GetString()).IsEqualTo("3f0c2d9e-aaaa-4bbb-8ccc-0123456789ab");
        await Assert.That(data.GetProperty("reportingState").GetString()).IsEqualTo("error");
        await Assert.That(data.GetProperty("reporterError").GetString()).IsEqualTo("tls_ca_changed");

        JsonElement nodes = data.GetProperty("nodes");
        JsonElement cp = nodes.EnumerateArray().Single(n => n.GetProperty("nodeName").GetString() == "cp-1");
        await Assert.That(cp.GetProperty("roles")[0].GetString()).IsEqualTo("control-plane");
        await Assert.That(cp.GetProperty("roles")[1].GetString()).IsEqualTo("etcd");
        await Assert.That(cp.GetProperty("readyStatus").GetString()).IsEqualTo("True");
        await Assert.That(cp.GetProperty("readySince").GetDateTimeOffset()).IsEqualTo(T0.AddDays(1));
        await Assert.That(cp.GetProperty("conditions").GetArrayLength()).IsEqualTo(0);
        await Assert.That(cp.GetProperty("unschedulable").GetBoolean()).IsFalse();
        await Assert.That(cp.GetProperty("containerRuntimeVersion").GetString()).IsEqualTo("containerd://1.7.23");
        await Assert.That(cp.GetProperty("podCount").GetInt32()).IsEqualTo(14);
        await Assert.That(cp.GetProperty("podCapacity").GetInt32()).IsEqualTo(110);
        await Assert.That(cp.GetProperty("lastReporterSeenAt").GetDateTimeOffset()).IsEqualTo(snapshot);
        await Assert.That(cp.GetProperty("kubeletSkew").GetBoolean()).IsFalse();
        JsonElement worker = nodes.EnumerateArray().Single(n => n.GetProperty("nodeName").GetString() == "worker-1");
        await Assert.That(worker.GetProperty("conditions")[0].GetProperty("type").GetString()).IsEqualTo("MemoryPressure");
        await Assert.That(worker.GetProperty("conditions")[0].GetProperty("status").GetString()).IsEqualTo("True");
        await Assert.That(worker.GetProperty("conditions")[0].GetProperty("lastTransitionTime").GetString()).IsEqualTo("2026-10-12T10:00:00Z");
        await Assert.That(worker.GetProperty("unschedulable").GetBoolean()).IsTrue();
        await Assert.That(worker.GetProperty("unschedulableSince").GetDateTimeOffset()).IsEqualTo(T0.AddDays(1));
        await Assert.That(worker.GetProperty("kubeletSkew").GetBoolean()).IsTrue();

        JsonElement candidates = data.GetProperty("candidates");
        await Assert.That(candidates.GetArrayLength()).IsEqualTo(2);
        JsonElement activeReporter = candidates.EnumerateArray().Single(c => c.GetProperty("machineId").GetInt64() == activeReporterId);
        await Assert.That(activeReporter.GetProperty("machineName").GetString()).IsEqualTo("cp-host");
        await Assert.That(activeReporter.GetProperty("isActiveReporter").GetBoolean()).IsTrue();
        await Assert.That(activeReporter.GetProperty("reporterError").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(activeReporter.GetProperty("lastReportAt").GetDateTimeOffset()).IsEqualTo(snapshot);
        await Assert.That(activeReporter.GetProperty("online").ValueKind).IsEqualTo(JsonValueKind.False);
        JsonElement standby = candidates.EnumerateArray().Single(c => c.GetProperty("machineId").GetInt64() == standbyId);
        await Assert.That(standby.GetProperty("isActiveReporter").GetBoolean()).IsFalse();
        await Assert.That(standby.GetProperty("reporterError").GetString()).IsEqualTo("tls_ca_changed");

        JsonElement eligible = data.GetProperty("eligibleMachines");
        await Assert.That(eligible.EnumerateArray().Select(m => m.GetProperty("machineId").GetInt64()).ToArray())
            .IsEquivalentTo(new[] { activeReporterId, standbyId, controllerId });
        JsonElement activeReporterEligible = eligible.EnumerateArray().Single(m => m.GetProperty("machineId").GetInt64() == activeReporterId);
        await Assert.That(activeReporterEligible.GetProperty("machineName").GetString()).IsEqualTo("cp-host");
        await Assert.That(activeReporterEligible.GetProperty("online").GetBoolean()).IsFalse();
        await Assert.That(activeReporterEligible.GetProperty("isPaused").GetBoolean()).IsFalse();
        await Assert.That(activeReporterEligible.GetProperty("remoteCommandsEnabled").GetBoolean()).IsTrue();
        await Assert.That(activeReporterEligible.GetProperty("enrolled").GetBoolean()).IsTrue();
        await Assert.That(activeReporterEligible.GetProperty("authorizedSigningKeyIds")[0].GetInt32()).IsEqualTo(keyId);
        JsonElement controllerEligible = eligible.EnumerateArray().Single(m => m.GetProperty("machineId").GetInt64() == controllerId);
        await Assert.That(controllerEligible.GetProperty("enrolled").GetBoolean()).IsFalse();
        await Assert.That(controllerEligible.GetProperty("remoteCommandsEnabled").GetBoolean()).IsFalse();
        await Assert.That(controllerEligible.GetProperty("authorizedSigningKeyIds").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task ClusterDetail_OtherTenantsMachinesAndCandidates_NeverAppear()
    {
        // Intent: a control-plane machine of another tenant that reports the same CA must not be offered
        // for enrolment, listed as a candidate or named, whichever way it relates to this tenant's cluster.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenant1Id = await SeedTenantAsync(db, "Tenant 1");
        int tenant2Id = await SeedTenantAsync(db, "Tenant 2");
        int user1Id = await SeedUserAsync(db, tenant1Id, UserAccountRoles.Viewer);
        long ownMachineId = await SeedMachineAsync(db, tenant1Id, "own-cp");
        long foreignMachineId = await SeedMachineAsync(db, tenant2Id, "tenant2-secret-cp");
        long ownClusterId = await SeedClusterAsync(db, tenant1Id, FingerprintA);
        long foreignClusterId = await SeedClusterAsync(db, tenant2Id, FingerprintA);
        await SeedLocalReportAsync(db, tenant1Id, ownMachineId, FingerprintA);
        await SeedLocalReportAsync(db, tenant2Id, foreignMachineId, FingerprintA, enrolled: true);
        await SeedCandidateAsync(db, tenant2Id, foreignClusterId, foreignMachineId);

        HttpResponseMessage response = await ClientFor(factory, user1Id, tenant1Id, UserAccountRoles.Viewer)
            .GetAsync($"/api/v1/clusters/{ownClusterId}");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("candidates").GetArrayLength()).IsEqualTo(0);
        await Assert.That(data.GetProperty("eligibleMachines").EnumerateArray().Select(m => m.GetProperty("machineId").GetInt64()).ToArray())
            .IsEquivalentTo(new[] { ownMachineId });
        await Assert.That(body.Contains("tenant2-secret-cp", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task ClusterDetail_CapabilityStoreFails_Returns200WithRemoteCommandsUnknown()
    {
        // Intent: Redis holds only the capability bits, so an outage there must leave the cluster page
        // readable. The one fact it cost is reported as null, never as a disabled host.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long machineId = await SeedMachineAsync(db, tenantId, "cp-host");
        long clusterId = await SeedClusterAsync(db, tenantId, FingerprintA);
        await SeedNodeAsync(db, tenantId, clusterId, "cp-1", machineId, controlPlane: true);
        await SeedLocalReportAsync(db, tenantId, machineId, FingerprintA);
        InMemoryMachinePingService ping = (InMemoryMachinePingService)factory.Services.GetRequiredService<IMachinePingService>();
        await ping.SetAgentCapabilitiesAsync(machineId, 1);
        ping.ReadFailure = new InvalidOperationException("redis unavailable");
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/clusters/{clusterId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement eligible = doc.RootElement.GetProperty("data").GetProperty("eligibleMachines").EnumerateArray().Single();
        await Assert.That(eligible.GetProperty("machineId").GetInt64()).IsEqualTo(machineId);
        await Assert.That(eligible.GetProperty("remoteCommandsEnabled").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(eligible.GetProperty("machineName").GetString()).IsEqualTo("cp-host");
    }

    [Test]
    public async Task ClusterDetail_TwoClustersOfOneTenantSharingACa_OfferEachLinkedMachineToItsOwnClusterOnly()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long nodeOfFirst = await SeedMachineAsync(db, tenantId, "first-cp");
        long candidateOfSecond = await SeedMachineAsync(db, tenantId, "second-cp");
        long unlinked = await SeedMachineAsync(db, tenantId, "unlinked-cp");
        long first = await SeedClusterAsync(db, tenantId, FingerprintA);
        long second = await SeedClusterAsync(db, tenantId, FingerprintA);
        await SeedNodeAsync(db, tenantId, first, "first-1", nodeOfFirst, controlPlane: true);
        await SeedCandidateAsync(db, tenantId, second, candidateOfSecond);
        foreach (long machineId in new[] { nodeOfFirst, candidateOfSecond, unlinked })
        {
            await SeedLocalReportAsync(db, tenantId, machineId, FingerprintA);
        }

        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        async Task<long[]> EligibleOfAsync(long clusterId)
        {
            HttpResponseMessage response = await client.GetAsync($"/api/v1/clusters/{clusterId}");
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            return doc.RootElement.GetProperty("data").GetProperty("eligibleMachines").EnumerateArray()
                .Select(m => m.GetProperty("machineId").GetInt64()).Order().ToArray();
        }

        await Assert.That(await EligibleOfAsync(first)).IsEquivalentTo(new[] { nodeOfFirst, unlinked }.Order().ToArray());
        await Assert.That(await EligibleOfAsync(second)).IsEquivalentTo(new[] { candidateOfSecond, unlinked }.Order().ToArray());
    }

    // ========== Rename ==========

    [Test]
    public async Task ClusterRename_AsMachineAdmin_Returns200AndWritesOneAuditRow()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.MachineAdmin);
        long clusterId = await SeedClusterAsync(db, tenantId, FingerprintA);
        await SeedNodeAsync(db, tenantId, clusterId, "cp-1", controlPlane: true);
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.MachineAdmin);

        HttpResponseMessage response = await client.PatchAsJsonAsync($"/api/v1/clusters/{clusterId}", new { displayName = "  Production  " });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("id").GetInt64()).IsEqualTo(clusterId);
        await Assert.That(data.GetProperty("name").GetString()).IsEqualTo("Production");
        await Assert.That(data.GetProperty("displayName").GetString()).IsEqualTo("Production");
        await Assert.That(data.GetProperty("nodeCount").GetInt32()).IsEqualTo(1);

        KubernetesCluster stored = await db.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
        await Assert.That(stored.DisplayName).IsEqualTo("Production");
        List<AuditLogEntry> audit = await db.AuditLog.Where(a => a.Action == AuditAction.KubernetesClusterRenamed).ToListAsync();
        await Assert.That(audit.Count).IsEqualTo(1);
        await Assert.That(audit[0].ResourceType).IsEqualTo(AuditResourceType.KubernetesCluster);
        await Assert.That(audit[0].ResourceId).IsEqualTo(clusterId.ToString());
        await Assert.That(audit[0].TenantId).IsEqualTo(tenantId);
        await Assert.That(audit[0].UserId).IsEqualTo(userId);
    }

    [Test]
    public async Task ClusterRename_NullDisplayName_ResetsToTheDerivedName()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.MachineAdmin);
        long clusterId = await SeedClusterAsync(db, tenantId, FingerprintA, displayName: "Production");
        await SeedNodeAsync(db, tenantId, clusterId, "cp-1", controlPlane: true);
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.MachineAdmin);

        HttpResponseMessage response = await client.PatchAsJsonAsync($"/api/v1/clusters/{clusterId}", new { displayName = (string?)null });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("name").GetString()).IsEqualTo("k3s cp-1");
        await Assert.That(data.GetProperty("displayName").ValueKind).IsEqualTo(JsonValueKind.Null);
        KubernetesCluster stored = await db.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
        await Assert.That(stored.DisplayName).IsNull();
    }

    [Test]
    public async Task ClusterRename_WhitespaceDisplayName_ResetsToTheDerivedName()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.MachineAdmin);
        long clusterId = await SeedClusterAsync(db, tenantId, FingerprintA, displayName: "Production");
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.MachineAdmin);

        HttpResponseMessage response = await client.PatchAsJsonAsync($"/api/v1/clusters/{clusterId}", new { displayName = "   " });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        KubernetesCluster stored = await db.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
        await Assert.That(stored.DisplayName).IsNull();
    }

    [Test]
    public async Task ClusterRename_NameOverOneHundredCharacters_Returns400AndChangesNothing()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.MachineAdmin);
        long clusterId = await SeedClusterAsync(db, tenantId, FingerprintA, displayName: "Production");
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.MachineAdmin);

        HttpResponseMessage response = await client.PatchAsJsonAsync($"/api/v1/clusters/{clusterId}", new { displayName = new string('x', 101) });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        KubernetesCluster stored = await db.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
        await Assert.That(stored.DisplayName).IsEqualTo("Production");
        int auditRows = await db.AuditLog.CountAsync(a => a.Action == AuditAction.KubernetesClusterRenamed);
        await Assert.That(auditRows).IsEqualTo(0);
    }

    [Test]
    public async Task ClusterRename_AsViewer_Returns403AndChangesNothing()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long clusterId = await SeedClusterAsync(db, tenantId, FingerprintA);
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.PatchAsJsonAsync($"/api/v1/clusters/{clusterId}", new { displayName = "Nope" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        KubernetesCluster stored = await db.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
        await Assert.That(stored.DisplayName).IsNull();
        int auditRows = await db.AuditLog.CountAsync(a => a.Action == AuditAction.KubernetesClusterRenamed);
        await Assert.That(auditRows).IsEqualTo(0);
    }

    // ========== Cross-tenant isolation ==========

    [Test]
    public async Task ClusterEndpoints_OtherTenantsCluster_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenant1Id = await SeedTenantAsync(db, "Tenant 1");
        int tenant2Id = await SeedTenantAsync(db, "Tenant 2");
        int adminId = await SeedUserAsync(db, tenant1Id, UserAccountRoles.MachineAdmin);
        long foreignMachineId = await SeedMachineAsync(db, tenant2Id, "secret-host");
        long foreignClusterId = await SeedClusterAsync(db, tenant2Id, FingerprintA, displayName: "Secret cluster");
        await SeedNodeAsync(db, tenant2Id, foreignClusterId, "secret-node", foreignMachineId, controlPlane: true);
        HttpClient client = ClientFor(factory, adminId, tenant1Id, UserAccountRoles.MachineAdmin);

        HttpResponseMessage get = await client.GetAsync($"/api/v1/clusters/{foreignClusterId}");
        HttpResponseMessage patch = await client.PatchAsJsonAsync($"/api/v1/clusters/{foreignClusterId}", new { displayName = "Hijacked" });

        await Assert.That(get.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        string getBody = await get.Content.ReadAsStringAsync();
        string patchBody = await patch.Content.ReadAsStringAsync();
        foreach (string body in new[] { getBody, patchBody })
        {
            await Assert.That(body.Contains("Secret cluster", StringComparison.Ordinal)).IsFalse();
            await Assert.That(body.Contains("secret-node", StringComparison.Ordinal)).IsFalse();
            await Assert.That(body.Contains("secret-host", StringComparison.Ordinal)).IsFalse();
            await Assert.That(body.Contains(FingerprintA, StringComparison.Ordinal)).IsFalse();
        }

        KubernetesCluster stored = await db.KubernetesClusters.SingleAsync(c => c.Id == foreignClusterId);
        await Assert.That(stored.DisplayName).IsEqualTo("Secret cluster");
        int auditRows = await db.AuditLog.CountAsync(a => a.Action == AuditAction.KubernetesClusterRenamed);
        await Assert.That(auditRows).IsEqualTo(0);
    }

    // ========== Machine detail: Kubernetes block ==========

    [Test]
    public async Task MachineDetail_WithLinkedNode_ReturnsKubernetesBlockWithoutTheCaFingerprint()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long machineId = await SeedMachineAsync(db, tenantId, "cp-host");
        long clusterId = await SeedClusterAsync(db, tenantId, FingerprintA, displayName: "Production");
        await SeedNodeAsync(db, tenantId, clusterId, "cp-1", machineId, controlPlane: true);
        await db.InsertAsync(new MachineTelemetry
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = 14,
            Payload = $$"""{"distro":"k3s","kubelet_version":"v1.30.4+k3s1","node_name":"cp-1","node_name_source":"hostname","has_kubelet":true,"control_plane":true,"ca_fingerprint":"{{FingerprintA}}","admin_credential_present":true,"reporter_enrolled":false}""",
            ReceivedAt = DateTimeOffset.UtcNow,
            ServerReceivedAt = DateTimeOffset.UtcNow,
            SourceEventId = Guid.NewGuid().ToString("N"),
        });
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/detail");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement kubernetes = doc.RootElement.GetProperty("data").GetProperty("kubernetes");
        await Assert.That(kubernetes.GetProperty("grouping").GetString()).IsEqualTo("grouped");
        await Assert.That(kubernetes.GetProperty("clusterId").GetInt64()).IsEqualTo(clusterId);
        await Assert.That(kubernetes.GetProperty("clusterName").GetString()).IsEqualTo("Production");
        await Assert.That(kubernetes.GetProperty("node").GetProperty("nodeName").GetString()).IsEqualTo("cp-1");
        JsonElement local = kubernetes.GetProperty("local");
        await Assert.That(local.GetProperty("distro").GetString()).IsEqualTo("k3s");
        await Assert.That(local.GetProperty("kubeletVersion").GetString()).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(local.GetProperty("nodeName").GetString()).IsEqualTo("cp-1");
        await Assert.That(local.GetProperty("nodeNameSource").GetString()).IsEqualTo("hostname");
        await Assert.That(local.GetProperty("hasKubelet").GetBoolean()).IsTrue();
        await Assert.That(local.GetProperty("controlPlane").GetBoolean()).IsTrue();
        await Assert.That(local.GetProperty("adminCredentialPresent").GetBoolean()).IsTrue();
        await Assert.That(local.GetProperty("reporterEnrolled").GetBoolean()).IsFalse();
        await Assert.That(body.Contains(FingerprintA, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task MachineDetail_WithoutKubernetes_ReturnsNullKubernetesBlock()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long machineId = await SeedMachineAsync(db, tenantId, "plain-host");
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/detail");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(doc.RootElement.GetProperty("data").GetProperty("kubernetes").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task MachineDetail_ActiveReporterCandidateWithoutANode_ReturnsCandidateStateInItsCluster()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long machineId = await SeedMachineAsync(db, tenantId, "k0s-controller");
        DateTimeOffset reported = DateTimeOffset.UtcNow.AddMinutes(-4);
        long clusterId = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = FingerprintA,
            DisplayName = "Production",
            Distro = "k0s",
            FirstSeenAt = T0,
            LastSeenAt = T0,
            ActiveReporterMachineId = machineId,
            LastSnapshotReceivedAt = reported,
        });
        await SeedClusterAsync(db, tenantId, FingerprintA);
        await SeedCandidateAsync(db, tenantId, clusterId, machineId, error: "credentials_rejected", lastReportAt: reported);
        await SeedLocalReportAsync(db, tenantId, machineId, FingerprintA, enrolled: true);
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/detail");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement kubernetes = doc.RootElement.GetProperty("data").GetProperty("kubernetes");
        await Assert.That(kubernetes.GetProperty("grouping").GetString()).IsEqualTo("grouped");
        await Assert.That(kubernetes.GetProperty("clusterId").GetInt64()).IsEqualTo(clusterId);
        await Assert.That(kubernetes.GetProperty("clusterName").GetString()).IsEqualTo("Production");
        await Assert.That(kubernetes.GetProperty("node").ValueKind).IsEqualTo(JsonValueKind.Null);
        JsonElement candidate = kubernetes.GetProperty("candidate");
        await Assert.That(candidate.GetProperty("clusterId").GetInt64()).IsEqualTo(clusterId);
        await Assert.That(candidate.GetProperty("isActiveReporter").GetBoolean()).IsTrue();
        await Assert.That(candidate.GetProperty("reporterError").GetString()).IsEqualTo("credentials_rejected");
        await Assert.That(candidate.GetProperty("lastReportAt").GetDateTimeOffset()).IsEqualTo(reported);
    }

    [Test]
    public async Task MachineDetail_ActiveReporterWhoseSnapshotIsStale_IsNotReportedAsActive()
    {
        // Intent: the cluster still names the machine as the last sender, but its snapshot is hours old,
        // so the page must not claim the machine is reporting.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long machineId = await SeedMachineAsync(db, tenantId, "k0s-controller");
        DateTimeOffset snapshot = DateTimeOffset.UtcNow.AddHours(-5);
        long clusterId = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = FingerprintA,
            DisplayName = "Production",
            Distro = "k0s",
            FirstSeenAt = T0,
            LastSeenAt = T0,
            ActiveReporterMachineId = machineId,
            LastSnapshotReceivedAt = snapshot,
        });
        await SeedCandidateAsync(db, tenantId, clusterId, machineId, lastReportAt: snapshot);
        await SeedLocalReportAsync(db, tenantId, machineId, FingerprintA, enrolled: true);
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/detail");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement candidate = doc.RootElement.GetProperty("data").GetProperty("kubernetes").GetProperty("candidate");
        await Assert.That(candidate.GetProperty("isActiveReporter").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task ClusterDetail_ActiveReporterWhoseSnapshotIsStale_IsNotReportedAsActiveWhileTheStateSaysStale()
    {
        // Intent: the candidate flag and the cluster's derived reporting state judge the same snapshot, so
        // they cannot disagree: a stale cluster has no machine marked as reporting.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long machineId = await SeedMachineAsync(db, tenantId, "cp-host");
        DateTimeOffset snapshot = DateTimeOffset.UtcNow.AddHours(-5);
        long clusterId = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = FingerprintA,
            Distro = "k3s",
            FirstSeenAt = T0,
            LastSeenAt = T0,
            ActiveReporterMachineId = machineId,
            LastSnapshotReceivedAt = snapshot,
        });
        await SeedCandidateAsync(db, tenantId, clusterId, machineId, lastReportAt: snapshot);
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/clusters/{clusterId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("reportingState").GetString()).IsEqualTo("stale");
        await Assert.That(data.GetProperty("candidates")[0].GetProperty("isActiveReporter").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task MachineDetail_NodeNotACandidate_ReturnsNullCandidate()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, "Tenant 1");
        int userId = await SeedUserAsync(db, tenantId, UserAccountRoles.Viewer);
        long machineId = await SeedMachineAsync(db, tenantId, "worker-host");
        long clusterId = await SeedClusterAsync(db, tenantId, FingerprintA);
        await SeedNodeAsync(db, tenantId, clusterId, "worker-1", machineId);
        HttpClient client = ClientFor(factory, userId, tenantId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/detail");

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement kubernetes = doc.RootElement.GetProperty("data").GetProperty("kubernetes");
        await Assert.That(kubernetes.GetProperty("candidate").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(kubernetes.GetProperty("node").GetProperty("kubeletSkew").GetBoolean()).IsFalse();
    }
}
