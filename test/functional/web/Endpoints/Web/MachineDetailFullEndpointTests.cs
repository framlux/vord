// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the machine detail full endpoint.
/// </summary>
public sealed class MachineDetailFullEndpointTests
{
    private static async Task<(int TenantId, int UserId, long MachineId)> SeedEnvironment(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Detail Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
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
            ExternalId = $"ext-detail-{Guid.NewGuid():N}",
            Username = $"detail-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
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
            Name = $"machine-detail-test",
            SerialNumber = "sn-detail-001",
            SystemId = "sid-detail-001",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenant.Id
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        // Seed summary and detail rows
        MachineStateSummary summary = new()
        {
            MachineId = machine.Id,
            TenantId = tenant.Id,
            Name = machine.Name,
            OperatingSystem = (byte)OperatingSystems.Ubuntu,
            MachineType = (byte)MachineTypes.BareMetalServer,
            Hostname = "test-host",
            CpuUsagePercent = 45,
            MemoryUsagePercent = 60,
            HealthStatus = 0,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        await db.InsertAsync(summary);

        MachineStateDetail detail = new()
        {
            MachineId = machine.Id,
            CpuBrand = "Intel Xeon",
            CpuCores = 8,
            MemoryTotalBytes = 17179869184,
        };
        await db.InsertAsync(detail);

        return (tenant.Id, user.Id, machine.Id);
    }

    [Test]
    public async Task FullDetail_ValidMachine_ReturnsCompleteResponse()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/detail");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        bool success = doc.RootElement.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        // Verify the seeded machine identity and summary data appears in the response
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("id").GetInt64()).IsEqualTo(machineId);
        await Assert.That(data.GetProperty("name").GetString()).IsEqualTo("machine-detail-test");
        await Assert.That(data.GetProperty("hostname").GetString()).IsEqualTo("test-host");
    }

    [Test]
    public async Task FullDetail_MachineReportedAgentVersion_IsReturned()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        await db.InsertAsync(new MachineTelemetry
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = 13,
            Payload = """{"version":"1.16.0"}""",
            ReceivedAt = DateTimeOffset.UtcNow,
            ServerReceivedAt = DateTimeOffset.UtcNow,
            SourceEventId = Guid.NewGuid().ToString("N"),
        });

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/detail");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("agentVersion").GetString()).IsEqualTo("1.16.0");
    }

    [Test]
    public async Task FullDetail_MachineNeverReportedAgentVersion_ReturnsNull()
    {
        // Intent: a machine with no agent version telemetry reports null rather than an empty
        // string, so the UI can distinguish "not reported yet" from a blank version.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/detail");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("agentVersion").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task FullDetail_CpuAndMemoryInfoTelemetry_ReturnsTheBreakdownAndSwapFigures()
    {
        // Both groups were already on the wire and already stored; only the response DTO dropped
        // them. Asserting the exact values (not just a 200) is what would catch a silent rename of
        // a snake_case field, which would otherwise bind to a plausible-looking zero.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        // Relative to now so the row stays inside the endpoint's seven-day recency window whatever
        // day this runs, and offset from the CPU row so the assertion below can only pass if the
        // response carries the memory record's own timestamp.
        DateTimeOffset memoryInfoReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-30);

        await db.InsertAsync(new MachineTelemetry
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = 6,
            Payload = """{"cpu_usage_percent":72,"user_time":24,"system_time":9,"nice_time":0,"idle_time":28,"iowait_time":37,"irq_time":0,"softirq_time":1,"steal_time":9}""",
            ReceivedAt = DateTimeOffset.UtcNow,
            ServerReceivedAt = DateTimeOffset.UtcNow,
            SourceEventId = Guid.NewGuid().ToString("N"),
        });

        await db.InsertAsync(new MachineTelemetry
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = 4,
            Payload = """{"memory_total":17179869184,"memory_free":2147483648,"memory_available":4294967296,"swap_total":8589934592,"swap_free":6442450944}""",
            ReceivedAt = memoryInfoReceivedAt,
            ServerReceivedAt = memoryInfoReceivedAt,
            SourceEventId = Guid.NewGuid().ToString("N"),
        });

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/detail");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        JsonElement cpu = data.GetProperty("cpuUsage");
        await Assert.That(cpu.GetProperty("cpuUsagePercent").GetInt32()).IsEqualTo(72);
        await Assert.That(cpu.GetProperty("userTime").GetInt32()).IsEqualTo(24);
        await Assert.That(cpu.GetProperty("iowaitTime").GetInt32()).IsEqualTo(37);
        await Assert.That(cpu.GetProperty("stealTime").GetInt32()).IsEqualTo(9);

        JsonElement memoryInfo = data.GetProperty("memoryInfo");
        await Assert.That(memoryInfo.GetProperty("memoryAvailable").GetInt64()).IsEqualTo(4294967296L);
        await Assert.That(memoryInfo.GetProperty("swapTotal").GetInt64()).IsEqualTo(8589934592L);
        await Assert.That(memoryInfo.GetProperty("swapFree").GetInt64()).IsEqualTo(6442450944L);

        DateTimeOffset returnedReceivedAt = data.GetProperty("memoryInfoReceivedAt").GetDateTimeOffset();
        await Assert.That(returnedReceivedAt.ToUnixTimeSeconds())
            .IsEqualTo(memoryInfoReceivedAt.ToUnixTimeSeconds());
    }

    [Test]
    public async Task FullDetail_NoMemoryInfoTelemetry_ReturnsNullMemoryInfoWithoutFailing()
    {
        // The slow-tick record can be absent for the first fifteen minutes of a machine's life, and
        // the response must still be a 200 with an explicit null rather than a 500.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/detail");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("memoryInfo").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(data.GetProperty("memoryInfoReceivedAt").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task FullDetail_MachineNotFound_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/999999/detail");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task FullDetail_CrossTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        // Create a second tenant + user
        Tenant otherTenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = "Other Tenant",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        otherTenant.Id = await db.InsertWithInt32IdentityAsync(otherTenant);

        TenantSubscription otherSub = new()
        {
            TenantId = otherTenant.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(otherSub);

        UserAccount otherUser = new()
        {
            ExternalId = $"ext-other-{Guid.NewGuid():N}",
            Username = $"other-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        otherUser.Id = await db.InsertWithInt32IdentityAsync(otherUser);

        UserTenantRole otherRole = new()
        {
            UserId = otherUser.Id,
            AssignedTenantId = otherTenant.Id,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = otherUser.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(otherRole);

        // Other tenant's user trying to access first tenant's machine
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(otherUser.Id)
            .WithRole(otherTenant.Id, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(otherTenant.Id)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/detail");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task FullDetail_Unauthenticated_ReturnsUnauthorized()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/1/detail");

        bool isRejected = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                          (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isRejected).IsTrue();
    }
}
