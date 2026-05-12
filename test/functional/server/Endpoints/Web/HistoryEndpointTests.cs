// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for machine history endpoints (CPU, Memory, Disk, SSH, Services).
/// Each test creates its own isolated factory and database to ensure test independence.
/// </summary>
public sealed class HistoryEndpointTests
{
    /// <summary>
    /// Seeds a complete test environment with a tenant, subscription, user, machine,
    /// machine state summary, and telemetry rows for CPU, Memory, Disk, SSH, and ServiceStatus.
    /// </summary>
    private static async Task<(int TenantId, int UserId, long MachineId)> SeedEnvironment(
        DatabaseContext db, SubscriptionTier tier = SubscriptionTier.Pro)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"History Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-history-{Guid.NewGuid():N}",
            Username = $"history-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        UserTenantRole role = new()
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N").PadLeft(64, '0'),
            Name = "history-test-machine",
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenant.Id,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary summary = new()
        {
            MachineId = machine.Id,
            TenantId = tenant.Id,
            Name = machine.Name,
            OperatingSystem = (byte)OperatingSystems.Ubuntu,
            MachineType = (byte)MachineTypes.BareMetalServer,
            Hostname = "history-host",
            CpuUsagePercent = 45,
            MemoryUsagePercent = 62,
            HealthStatus = 0,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        await db.InsertAsync(summary);

        // Seed telemetry rows within the last hour so they fall within the 24h range
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Serialize payloads using the same JsonDefaults.SnakeCase used by the production
        // telemetry pipeline, so these tests will catch naming policy mismatches.

        // CPU telemetry (type 6)
        MachineTelemetry cpuTelemetry = new()
        {
            MachineId = machine.Id,
            TenantId = tenant.Id,
            TelemetryType = 6,
            Payload = JsonSerializer.Serialize(new CpuUsagePayload { CpuUsagePercent = 45 }, JsonDefaults.SnakeCase),
            ReceivedAt = now.AddMinutes(-10),
        };
        await db.InsertAsync(cpuTelemetry);

        // Memory telemetry (type 7)
        MachineTelemetry memoryTelemetry = new()
        {
            MachineId = machine.Id,
            TenantId = tenant.Id,
            TelemetryType = 7,
            Payload = JsonSerializer.Serialize(new MemoryUsagePayload { MemoryTotal = 16000000000, MemoryUsed = 10000000000, MemoryUsagePercent = 62 }, JsonDefaults.SnakeCase),
            ReceivedAt = now.AddMinutes(-10),
        };
        await db.InsertAsync(memoryTelemetry);

        // Disk telemetry (type 8)
        MachineTelemetry diskTelemetry = new()
        {
            MachineId = machine.Id,
            TenantId = tenant.Id,
            TelemetryType = 8,
            Payload = JsonSerializer.Serialize(new DiskUsagePayload
            {
                Disks =
                [
                    new DiskUsageEntryDto
                    {
                        Device = "/dev/sda1",
                        Path = "/",
                        BlocksSize = 4096,
                        Blocks = 1000000,
                        BlocksFree = 500000,
                        BlocksAvailable = 480000,
                        BlocksUsed = 500000,
                        UsagePercent = 50
                    }
                ]
            }, JsonDefaults.SnakeCase),
            ReceivedAt = now.AddMinutes(-10),
        };
        await db.InsertAsync(diskTelemetry);

        // SSH telemetry (type 9)
        MachineTelemetry sshTelemetry = new()
        {
            MachineId = machine.Id,
            TenantId = tenant.Id,
            TelemetryType = 9,
            Payload = JsonSerializer.Serialize(new SshSessionPayload
            {
                User = "root",
                SourceIp = "10.0.1.5",
                SourcePort = 54321,
                Action = "login",
                AuthMethod = "publickey",
                Timestamp = "2026-05-06T10:00:00Z"
            }, JsonDefaults.SnakeCase),
            ReceivedAt = now.AddMinutes(-10),
        };
        await db.InsertAsync(sshTelemetry);

        // ServiceStatus telemetry (type 12)
        MachineTelemetry servicesTelemetry = new()
        {
            MachineId = machine.Id,
            TenantId = tenant.Id,
            TelemetryType = 12,
            Payload = JsonSerializer.Serialize(new ServiceStatusPayload
            {
                Services =
                [
                    new ServiceEntryDto { Unit = "nginx.service", LoadState = "loaded", ActiveState = "active", SubState = "running", Description = "Nginx" },
                    new ServiceEntryDto { Unit = "redis.service", LoadState = "loaded", ActiveState = "failed", SubState = "dead", Description = "Redis" }
                ]
            }, JsonDefaults.SnakeCase),
            ReceivedAt = now.AddMinutes(-10),
        };
        await db.InsertAsync(servicesTelemetry);

        return (tenant.Id, user.Id, machine.Id);
    }

    private static HttpClient BuildAuthenticatedClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // ──────────────────────────────────────────────────────────────
    // 1. CPU History - Valid Request
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task CpuHistory_ValidRequest_Returns200WithData()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/history/cpu?range=24h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();

        JsonElement data = root.GetProperty("data");

        // Verify the response contains the expected structure for CPU history
        await Assert.That(data.TryGetProperty("points", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("stats", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("bucketSeconds", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("rawPointCount", out _)).IsTrue();

        // Verify that data points were returned from our seeded telemetry
        await Assert.That(data.GetProperty("rawPointCount").GetInt32()).IsGreaterThanOrEqualTo(1);

        // Verify stats structure contains expected fields
        JsonElement stats = data.GetProperty("stats");
        await Assert.That(stats.TryGetProperty("min", out _)).IsTrue();
        await Assert.That(stats.TryGetProperty("avg", out _)).IsTrue();
        await Assert.That(stats.TryGetProperty("max", out _)).IsTrue();
        await Assert.That(stats.TryGetProperty("p95", out _)).IsTrue();
    }

    // ──────────────────────────────────────────────────────────────
    // 2. Memory History - Valid Request
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task MemoryHistory_ValidRequest_Returns200WithData()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/history/memory?range=24h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();

        JsonElement data = root.GetProperty("data");

        // Verify the response contains the expected structure for memory history
        await Assert.That(data.TryGetProperty("points", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("stats", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("bucketSeconds", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("rawPointCount", out _)).IsTrue();

        // Verify that data points were returned from our seeded telemetry
        await Assert.That(data.GetProperty("rawPointCount").GetInt32()).IsGreaterThanOrEqualTo(1);
    }

    // ──────────────────────────────────────────────────────────────
    // 3. Disk History - Valid Request with Multi-Series
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task DiskHistory_ValidRequest_Returns200WithMultiSeries()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/history/disk?range=24h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();

        JsonElement data = root.GetProperty("data");

        // Verify the disk response uses multi-series structure
        await Assert.That(data.TryGetProperty("series", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("bucketSeconds", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("rawPointCount", out _)).IsTrue();

        JsonElement series = data.GetProperty("series");
        await Assert.That(series.GetArrayLength()).IsGreaterThanOrEqualTo(1);

        // Verify the first series has the expected structure
        JsonElement firstSeries = series[0];
        await Assert.That(firstSeries.TryGetProperty("device", out _)).IsTrue();
        await Assert.That(firstSeries.TryGetProperty("mountPoint", out _)).IsTrue();
        await Assert.That(firstSeries.TryGetProperty("points", out _)).IsTrue();
        await Assert.That(firstSeries.TryGetProperty("stats", out _)).IsTrue();

        // Verify the device matches our seeded data
        await Assert.That(firstSeries.GetProperty("device").GetString()).IsEqualTo("/dev/sda1");
        await Assert.That(firstSeries.GetProperty("mountPoint").GetString()).IsEqualTo("/");
    }

    // ──────────────────────────────────────────────────────────────
    // 4. SSH History - Valid Request with Events
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task SshHistory_ValidRequest_Returns200WithEvents()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/history/ssh?range=24h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();

        JsonElement data = root.GetProperty("data");

        // Verify the SSH response uses events structure
        await Assert.That(data.TryGetProperty("events", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("totalEvents", out _)).IsTrue();

        JsonElement events = data.GetProperty("events");
        await Assert.That(events.GetArrayLength()).IsGreaterThanOrEqualTo(1);

        // Verify the first event has expected SSH session fields
        JsonElement firstEvent = events[0];
        await Assert.That(firstEvent.GetProperty("user").GetString()).IsEqualTo("root");
        await Assert.That(firstEvent.GetProperty("sourceIp").GetString()).IsEqualTo("10.0.1.5");
        await Assert.That(firstEvent.GetProperty("sourcePort").GetInt32()).IsEqualTo(54321);
        await Assert.That(firstEvent.GetProperty("action").GetString()).IsEqualTo("login");
        await Assert.That(firstEvent.GetProperty("authMethod").GetString()).IsEqualTo("publickey");
        await Assert.That(firstEvent.TryGetProperty("timestamp", out _)).IsTrue();
    }

    // ──────────────────────────────────────────────────────────────
    // 5. Service History - Valid Request with Failed Counts
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task ServiceHistory_ValidRequest_Returns200WithFailedCounts()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/history/services?range=24h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();

        JsonElement data = root.GetProperty("data");

        // Verify the service history response structure
        await Assert.That(data.TryGetProperty("points", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("stats", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("bucketSeconds", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("rawPointCount", out _)).IsTrue();

        // Verify we have at least one data point from the seeded telemetry
        await Assert.That(data.GetProperty("rawPointCount").GetInt32()).IsGreaterThanOrEqualTo(1);

        JsonElement points = data.GetProperty("points");
        await Assert.That(points.GetArrayLength()).IsGreaterThanOrEqualTo(1);

        // Verify the points contain failedCount and totalCount fields
        JsonElement firstPoint = points[0];
        await Assert.That(firstPoint.TryGetProperty("failedCount", out _)).IsTrue();
        await Assert.That(firstPoint.TryGetProperty("totalCount", out _)).IsTrue();

        // Our seeded data has 1 failed service (redis) out of 2 total
        await Assert.That(firstPoint.GetProperty("failedCount").GetInt32()).IsEqualTo(1);
        await Assert.That(firstPoint.GetProperty("totalCount").GetInt32()).IsEqualTo(2);
    }

    // ──────────────────────────────────────────────────────────────
    // 6. CPU History - Machine Not Found
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task CpuHistory_MachineNotFound_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        // Request with a machine ID that does not exist
        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/999999/history/cpu?range=24h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // ──────────────────────────────────────────────────────────────
    // 7. CPU History - Wrong Tenant Returns 404
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task CpuHistory_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Seed a machine in tenant A
        (int tenantAId, int _, long machineId) = await SeedEnvironment(db);

        // Create a separate tenant B with its own user
        Tenant tenantB = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Other Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenantB.Id = await db.InsertWithInt32IdentityAsync(tenantB);

        TenantSubscription subscriptionB = new()
        {
            TenantId = tenantB.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscriptionB);

        UserAccount userB = new()
        {
            ExternalId = $"ext-other-{Guid.NewGuid():N}",
            Username = $"other-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        userB.Id = await db.InsertWithInt32IdentityAsync(userB);

        UserTenantRole roleB = new()
        {
            UserId = userB.Id,
            AssignedTenantId = tenantB.Id,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = userB.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(roleB);

        // Authenticate as tenant B user and request tenant A's machine
        HttpClient client = BuildAuthenticatedClient(factory, tenantB.Id, userB.Id);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/history/cpu?range=24h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // ──────────────────────────────────────────────────────────────
    // 8. CPU History - No Telemetry Returns 200 with Empty Points
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task CpuHistory_NoTelemetry_Returns200WithEmptyPoints()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Seed a minimal environment without any telemetry data
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Empty Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-empty-{Guid.NewGuid():N}",
            Username = $"empty-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        UserTenantRole role = new()
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N").PadLeft(64, '0'),
            Name = "empty-machine",
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenant.Id,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary summary = new()
        {
            MachineId = machine.Id,
            TenantId = tenant.Id,
            Name = machine.Name,
            OperatingSystem = (byte)OperatingSystems.Ubuntu,
            MachineType = (byte)MachineTypes.BareMetalServer,
            Hostname = "empty-host",
            HealthStatus = 0,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        await db.InsertAsync(summary);

        HttpClient client = BuildAuthenticatedClient(factory, tenant.Id, user.Id);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machine.Id}/history/cpu?range=24h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();

        JsonElement data = root.GetProperty("data");

        // With no telemetry seeded, raw point count should be zero
        await Assert.That(data.GetProperty("rawPointCount").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("points").GetArrayLength()).IsEqualTo(0);
    }

    // ──────────────────────────────────────────────────────────────
    // 9. CPU History - Invalid Range Returns 400
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task CpuHistory_InvalidRange_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        // "2h" is not a valid range value (valid: 1h, 6h, 24h, 7d, 30d)
        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/history/cpu?range=2h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // Verify the error response indicates the range is invalid
        await Assert.That(root.GetProperty("success").GetBoolean()).IsFalse();
        string? message = root.GetProperty("message").GetString();
        await Assert.That(message).IsNotNull();
        await Assert.That(message!).Contains("Invalid range");
    }

    // ──────────────────────────────────────────────────────────────
    // 10. CPU History - Retention Exceeded Returns 403
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task CpuHistory_RetentionExceeded_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Free tier has 1 day retention, so requesting 7d should be denied
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db, SubscriptionTier.Free);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/history/cpu?range=7d");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // Verify the retention error response structure
        await Assert.That(root.GetProperty("upgradeRequired").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("currentRetentionDays").GetInt32()).IsEqualTo(1);

        string? message = root.GetProperty("message").GetString();
        await Assert.That(message).IsNotNull();
        await Assert.That(message!).Contains("higher subscription tier");
    }

    // ──────────────────────────────────────────────────────────────
    // 11. CPU History - Unauthenticated Returns 401
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task CpuHistory_Unauthenticated_Returns401()
    {
        using FunctionalTestFactory factory = new();

        // Create a client without any authentication headers
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/1/history/cpu?range=24h");

        // Unauthenticated requests to protected endpoints should return 401 or 302 redirect
        bool isRejected = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                          (response.StatusCode == HttpStatusCode.Found);

        await Assert.That(isRejected)
            .IsTrue()
            .Because($"Expected 401 or 302 for unauthenticated request, got {response.StatusCode}");
    }

    // ──────────────────────────────────────────────────────────────
    // 12. Memory History - Machine Not Found
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task MemoryHistory_MachineNotFound_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        // Request with a machine ID that does not exist
        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/999999/history/memory?range=24h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // ──────────────────────────────────────────────────────────────
    // 13. Memory History - Retention Exceeded Returns 403
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task MemoryHistory_RetentionExceeded_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Free tier has 1 day retention, so requesting 7d should be denied
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db, SubscriptionTier.Free);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/history/memory?range=7d");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // Verify the retention error response structure
        await Assert.That(root.GetProperty("upgradeRequired").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("currentRetentionDays").GetInt32()).IsEqualTo(1);

        string? message = root.GetProperty("message").GetString();
        await Assert.That(message).IsNotNull();
        await Assert.That(message!).Contains("higher subscription tier");
    }

    // ──────────────────────────────────────────────────────────────
    // 14. Disk History - Machine Not Found
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task DiskHistory_MachineNotFound_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        // Request with a machine ID that does not exist
        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/999999/history/disk?range=24h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // ──────────────────────────────────────────────────────────────
    // 15. Disk History - Retention Exceeded Returns 403
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task DiskHistory_RetentionExceeded_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Free tier has 1 day retention, so requesting 7d should be denied
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db, SubscriptionTier.Free);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/history/disk?range=7d");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // Verify the retention error response structure
        await Assert.That(root.GetProperty("upgradeRequired").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("currentRetentionDays").GetInt32()).IsEqualTo(1);

        string? message = root.GetProperty("message").GetString();
        await Assert.That(message).IsNotNull();
        await Assert.That(message!).Contains("higher subscription tier");
    }

    // ──────────────────────────────────────────────────────────────
    // 16. Services History - Machine Not Found
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task ServiceHistory_MachineNotFound_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        // Request with a machine ID that does not exist
        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/999999/history/services?range=24h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // ──────────────────────────────────────────────────────────────
    // 17. Services History - Retention Exceeded Returns 403
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task ServiceHistory_RetentionExceeded_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Free tier has 1 day retention, so requesting 7d should be denied
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db, SubscriptionTier.Free);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/history/services?range=7d");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // Verify the retention error response structure
        await Assert.That(root.GetProperty("upgradeRequired").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("currentRetentionDays").GetInt32()).IsEqualTo(1);

        string? message = root.GetProperty("message").GetString();
        await Assert.That(message).IsNotNull();
        await Assert.That(message!).Contains("higher subscription tier");
    }

    // ──────────────────────────────────────────────────────────────
    // 18. SSH History - Machine Not Found
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task SshHistory_MachineNotFound_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _) = await SeedEnvironment(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        // Request with a machine ID that does not exist
        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/999999/history/ssh?range=24h");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // ──────────────────────────────────────────────────────────────
    // 19. SSH History - Retention Exceeded Returns 403
    // ──────────────────────────────────────────────────────────────

    [Test]
    public async Task SshHistory_RetentionExceeded_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Free tier has 1 day retention, so requesting 7d should be denied
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db, SubscriptionTier.Free);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/history/ssh?range=7d");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // Verify the retention error response structure
        await Assert.That(root.GetProperty("upgradeRequired").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("currentRetentionDays").GetInt32()).IsEqualTo(1);

        string? message = root.GetProperty("message").GetString();
        await Assert.That(message).IsNotNull();
        await Assert.That(message!).Contains("higher subscription tier");
    }
}
