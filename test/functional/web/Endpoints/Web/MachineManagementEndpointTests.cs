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
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for machine management endpoints (delete, detail, status).
/// </summary>
public sealed class MachineManagementEndpointTests
{
    private static async Task<(int TenantId, int UserId, long MachineId, string MachineName)> SeedEnvironment(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Machine Tenant {Guid.NewGuid():N}",
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
            ExternalId = $"ext-machine-{Guid.NewGuid():N}",
            Username = $"machine-{Guid.NewGuid():N}@example.com",
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
            Role = UserAccountRoles.MachineAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        string machineName = $"test-machine-{Guid.NewGuid():N}";
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = machineName,
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

        return (tenant.Id, user.Id, machine.Id, machineName);
    }

    [Test]
    public async Task MachineDelete_OwnTenant_SoftDeletesAndReturnsSuccessPayload()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/machines/{machineId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        string message = root.GetProperty("message").GetString()!;
        await Assert.That(message).Contains("deleted");

        // Verify the machine is actually soft-deleted in the database
        Machine? deletedMachine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        await Assert.That(deletedMachine).IsNotNull();
        await Assert.That(deletedMachine!.IsDeleted).IsTrue();
    }

    [Test]
    public async Task MachineDelete_OtherTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1, long machineId1, string _) = await SeedEnvironment(db);
        (int tenantId2, int _, long _, string _) = await SeedEnvironment(db);

        // User from tenant 2 tries to delete machine from tenant 1.
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId1)
            .WithRole(tenantId2, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId2)
            .Build();

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/machines/{machineId1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        bool hasMessage = root.TryGetProperty("message", out JsonElement messageElement);
        await Assert.That(hasMessage).IsTrue();

        string message = messageElement.GetString()!;
        await Assert.That(string.IsNullOrWhiteSpace(message)).IsFalse();

        // Verify the machine was NOT deleted in the database
        Machine? machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId1);
        await Assert.That(machine).IsNotNull();
        await Assert.That(machine!.IsDeleted).IsFalse();
    }

    [Test]
    public async Task MachineDelete_NonExistentMachine_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/machines/99999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        bool hasMessage = root.TryGetProperty("message", out JsonElement messageElement);
        await Assert.That(hasMessage).IsTrue();

        string message = messageElement.GetString()!;
        await Assert.That(string.IsNullOrWhiteSpace(message)).IsFalse();
    }

    [Test]
    public async Task MachineDelete_NegativeMachineId_ReturnsErrorResponse()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/machines/-1");

        // Negative IDs should not match any machine, endpoint returns 404
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task MachineDelete_MalformedId_ReturnsBadRequestOrNotFound()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/machines/not-a-number");

        // Malformed IDs should be rejected; either 400 or 404 is acceptable
        bool isClientError = (response.StatusCode == HttpStatusCode.BadRequest) ||
                             (response.StatusCode == HttpStatusCode.NotFound);
        await Assert.That(isClientError).IsTrue();
    }

    [Test]
    public async Task MachineDetail_OwnTenant_ReturnsMachineFields()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string machineName) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        JsonElement data = root.GetProperty("data");
        long returnedId = data.GetProperty("id").GetInt64();
        await Assert.That(returnedId).IsEqualTo(machineId);

        string returnedName = data.GetProperty("name").GetString()!;
        await Assert.That(returnedName).IsEqualTo(machineName);

        string returnedSerial = data.GetProperty("serialNumber").GetString()!;
        await Assert.That(returnedSerial).IsNotNull();

        // Verify machine type is present and valid
        bool hasMachineType = data.TryGetProperty("machineType", out JsonElement _);
        await Assert.That(hasMachineType).IsTrue();

        // Verify operating system is present
        bool hasOs = data.TryGetProperty("operatingSystem", out JsonElement _);
        await Assert.That(hasOs).IsTrue();

        // Verify isDeleted is false for an active machine
        bool isDeleted = data.GetProperty("isDeleted").GetBoolean();
        await Assert.That(isDeleted).IsFalse();
    }

    [Test]
    public async Task MachineDetail_OtherTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1, long machineId1, string _) = await SeedEnvironment(db);
        (int tenantId2, int _, long _, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId1)
            .WithRole(tenantId2, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId2)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task MachineDetail_NonExistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/99999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task MachineDetail_MalformedId_ReturnsErrorResponse()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/abc");

        bool isClientError = (response.StatusCode == HttpStatusCode.BadRequest) ||
                             (response.StatusCode == HttpStatusCode.NotFound);
        await Assert.That(isClientError).IsTrue();
    }

    [Test]
    public async Task MachineStatus_OwnTenant_ReturnsStatusFields()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/status");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        JsonElement data = root.GetProperty("data");
        // Status should have isOnline field
        bool hasIsOnline = data.TryGetProperty("isOnline", out JsonElement isOnlineElement);
        await Assert.That(hasIsOnline).IsTrue();

        // Freshly registered machine should be offline
        bool isOnline = isOnlineElement.GetBoolean();
        await Assert.That(isOnline).IsFalse();
    }

    [Test]
    public async Task MachineStatus_OtherTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1, long machineId1, string _) = await SeedEnvironment(db);
        (int tenantId2, int _, long _, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId1)
            .WithRole(tenantId2, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId2)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId1}/status");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task MachineStatus_NonExistentId_Returns404WithErrorBody()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/99999/status");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        bool hasMessage = root.TryGetProperty("message", out JsonElement messageElement);
        await Assert.That(hasMessage).IsTrue();

        string message = messageElement.GetString()!;
        await Assert.That(string.IsNullOrWhiteSpace(message)).IsFalse();
    }
}
