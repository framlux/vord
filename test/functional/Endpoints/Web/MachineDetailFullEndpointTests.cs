// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
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
            ExternalId = $"ext-detail-{Guid.NewGuid():N}",
            Username = $"detail-{Guid.NewGuid():N}@example.com",
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
            CreatedByUserId = 0,
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
            CreatedByUserId = 0,
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
