// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the machine status polling endpoint.
/// </summary>
public sealed class MachineStatusEndpointTests
{
    private static async Task<(int TenantId, int UserId, long MachineId)> SeedEnvironment(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Status Tenant {Guid.NewGuid():N}",
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
            ExternalId = $"ext-status-{Guid.NewGuid():N}",
            Username = $"status-{Guid.NewGuid():N}@example.com",
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
            Name = "machine-status-test",
            SerialNumber = "sn-status-001",
            SystemId = "sid-status-001",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenant.Id
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        return (tenant.Id, user.Id, machine.Id);
    }

    [Test]
    public async Task Status_ValidMachine_ReturnsHealthStatusField()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        // Seed a summary so HealthComputer has telemetry to evaluate
        MachineStateSummary summary = new()
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = "machine-status-test",
            OperatingSystem = (byte)OperatingSystems.Ubuntu,
            MachineType = (byte)MachineTypes.BareMetalServer,
            Hostname = "test-host",
            CpuUsagePercent = 20,
            MemoryUsagePercent = 30,
            HealthStatus = 0,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        await db.InsertAsync(summary);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/status");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        bool success = doc.RootElement.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        JsonElement data = doc.RootElement.GetProperty("data");

        // Verify healthStatus is present in the response (regression: previously missing)
        await Assert.That(data.TryGetProperty("healthStatus", out JsonElement healthProp)).IsTrue();
        int healthValue = healthProp.GetInt32();

        // Machine has no recent ping via InMemoryPingService, so it should be Offline (3)
        await Assert.That(healthValue).IsEqualTo(3);
    }

    [Test]
    public async Task Status_OnlineMachineWithHealthySummary_ReturnsHealthy()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedEnvironment(db);

        MachineStateSummary summary = new()
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = "machine-status-test",
            OperatingSystem = (byte)OperatingSystems.Ubuntu,
            MachineType = (byte)MachineTypes.BareMetalServer,
            Hostname = "test-host",
            CpuUsagePercent = 20,
            MemoryUsagePercent = 30,
            HealthStatus = 0,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        await db.InsertAsync(summary);

        // Record a ping so the machine shows as online
        IMachinePingService pingService = factory.Services.GetRequiredService<IMachinePingService>();
        await pingService.RecordPingAsync(machineId);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/status");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        bool isOnline = data.GetProperty("isOnline").GetBoolean();
        int healthValue = data.GetProperty("healthStatus").GetInt32();

        await Assert.That(isOnline).IsTrue();
        await Assert.That(healthValue).IsEqualTo(0); // Healthy
    }

    [Test]
    public async Task Status_MachineNotFound_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/999999/status");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
