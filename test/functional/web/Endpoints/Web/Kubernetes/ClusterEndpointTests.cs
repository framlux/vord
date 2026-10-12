// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

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
        await Assert.That(cluster.GetProperty("reportingState").GetString()).IsEqualTo("identity_only");
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
}
