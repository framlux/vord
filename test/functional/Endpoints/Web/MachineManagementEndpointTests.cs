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
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for machine management endpoints (delete, detail, certificates, status).
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
            RetentionDays = 30,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-machine-{Guid.NewGuid():N}",
            Username = $"machine-{Guid.NewGuid():N}@example.com",
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
        await Assert.That(success).IsEqualTo(true);

        string message = root.GetProperty("message").GetString()!;
        await Assert.That(message).Contains("deleted");

        // Verify the machine is actually soft-deleted in the database
        Machine? deletedMachine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        await Assert.That(deletedMachine).IsNotNull();
        await Assert.That(deletedMachine!.IsDeleted).IsEqualTo(true);
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

        // Verify the machine was NOT deleted in the database
        Machine? machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId1);
        await Assert.That(machine).IsNotNull();
        await Assert.That(machine!.IsDeleted).IsEqualTo(false);
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
        await Assert.That(isClientError).IsEqualTo(true);
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
        await Assert.That(success).IsEqualTo(true);

        JsonElement data = root.GetProperty("data");
        long returnedId = data.GetProperty("id").GetInt64();
        await Assert.That(returnedId).IsEqualTo(machineId);

        string returnedName = data.GetProperty("name").GetString()!;
        await Assert.That(returnedName).IsEqualTo(machineName);

        string returnedSerial = data.GetProperty("serialNumber").GetString()!;
        await Assert.That(returnedSerial).IsNotNull();

        // Verify machine type is present and valid
        bool hasMachineType = data.TryGetProperty("machineType", out JsonElement _);
        await Assert.That(hasMachineType).IsEqualTo(true);

        // Verify operating system is present
        bool hasOs = data.TryGetProperty("operatingSystem", out JsonElement _);
        await Assert.That(hasOs).IsEqualTo(true);

        // Verify isDeleted is false for an active machine
        bool isDeleted = data.GetProperty("isDeleted").GetBoolean();
        await Assert.That(isDeleted).IsEqualTo(false);
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
        await Assert.That(isClientError).IsEqualTo(true);
    }

    [Test]
    public async Task MachineCertificates_OwnTenant_ReturnsCertificateFields()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        string thumbprint = $"thumb-{Guid.NewGuid():N}";
        DateTimeOffset issuedAt = DateTimeOffset.UtcNow.AddDays(-30);
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddDays(335);

        // Seed a certificate.
        await db.InsertAsync(new MachineCertificate
        {
            MachineId = machineId,
            Thumbprint = thumbprint,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
        });

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/certificates");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsEqualTo(true);

        JsonElement data = root.GetProperty("data");
        int totalCount = data.GetProperty("totalCount").GetInt32();
        await Assert.That(totalCount).IsEqualTo(1);

        JsonElement items = data.GetProperty("items");
        int itemCount = items.GetArrayLength();
        await Assert.That(itemCount).IsEqualTo(1);

        JsonElement cert = items[0];
        string returnedThumbprint = cert.GetProperty("thumbprint").GetString()!;
        await Assert.That(returnedThumbprint).IsEqualTo(thumbprint);

        bool isActive = cert.GetProperty("isActive").GetBoolean();
        await Assert.That(isActive).IsEqualTo(true);
    }

    [Test]
    public async Task MachineCertificates_InvalidMachineId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/99999/certificates");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
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
        await Assert.That(success).IsEqualTo(true);

        JsonElement data = root.GetProperty("data");
        // Status should have isOnline field
        bool hasIsOnline = data.TryGetProperty("isOnline", out JsonElement isOnlineElement);
        await Assert.That(hasIsOnline).IsEqualTo(true);

        // Freshly registered machine should be offline
        bool isOnline = isOnlineElement.GetBoolean();
        await Assert.That(isOnline).IsEqualTo(false);
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
}
