// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the machine update endpoint (PATCH /api/v1/machines/{id}).
/// </summary>
public sealed class MachineUpdateEndpointTests
{
    private static async Task<(int TenantId, int UserId, long MachineId, string MachineName)> SeedEnvironment(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Update Tenant {Guid.NewGuid():N}",
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
            ExternalId = $"ext-update-{Guid.NewGuid():N}",
            Username = $"update-{Guid.NewGuid():N}@example.com",
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
            Description = "Original description",
            Location = "Original location",
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

        // Create MachineStateSummary with telemetry hostname
        MachineStateSummary summary = new()
        {
            MachineId = machine.Id,
            TenantId = tenant.Id,
            Name = machineName,
            OperatingSystem = (byte)OperatingSystems.Ubuntu,
            MachineType = (byte)MachineTypes.BareMetalServer,
            Hostname = "telemetry-host.local",
            HealthStatus = 0,
        };
        await db.InsertAsync(summary);

        return (tenant.Id, user.Id, machine.Id, machineName);
    }

    private static async Task<(int TenantId, int UserId, long MachineId, string MachineName)> SeedEnvironmentWithHostname(
        DatabaseContext db, string? hostname)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Hostname Tenant {Guid.NewGuid():N}",
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
            ExternalId = $"ext-hostname-{Guid.NewGuid():N}",
            Username = $"hostname-{Guid.NewGuid():N}@example.com",
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

        string machineName = $"hostname-machine-{Guid.NewGuid():N}";
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = machineName,
            Description = "Hostname test machine",
            Location = "Hostname test location",
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

        // Create MachineStateSummary with the specified hostname (may be null)
        MachineStateSummary summary = new()
        {
            MachineId = machine.Id,
            TenantId = tenant.Id,
            Name = machineName,
            OperatingSystem = (byte)OperatingSystems.Ubuntu,
            MachineType = (byte)MachineTypes.BareMetalServer,
            Hostname = hostname,
            HealthStatus = 0,
        };
        await db.InsertAsync(summary);

        return (tenant.Id, user.Id, machine.Id, machineName);
    }

    // ──────────────────────────────────────────────
    // Happy-path tests
    // ──────────────────────────────────────────────

    [Test]
    public async Task UpdateMachine_ValidRequest_ReturnsUpdatedDto()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = "Updated Server", description = "New description", location = "New York" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        JsonElement data = root.GetProperty("data");
        string returnedName = data.GetProperty("name").GetString()!;
        await Assert.That(returnedName).IsEqualTo("Updated Server");

        string returnedDescription = data.GetProperty("description").GetString()!;
        await Assert.That(returnedDescription).IsEqualTo("New description");

        string returnedLocation = data.GetProperty("location").GetString()!;
        await Assert.That(returnedLocation).IsEqualTo("New York");

        // Verify the database row was updated
        Machine? dbMachine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        await Assert.That(dbMachine).IsNotNull();
        await Assert.That(dbMachine!.Name).IsEqualTo("Updated Server");
        await Assert.That(dbMachine.Description).IsEqualTo("New description");
        await Assert.That(dbMachine.Location).IsEqualTo("New York");

        // Verify MachineStateSummary.Name was synced
        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.Name).IsEqualTo("Updated Server");
    }

    [Test]
    public async Task UpdateMachine_OnlyNameChanged_PreservesOtherFields()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        // Send only a name change, preserving original description and location
        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = "Renamed Server", description = "Original description", location = "Original location" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        string returnedName = data.GetProperty("name").GetString()!;
        await Assert.That(returnedName).IsEqualTo("Renamed Server");

        string returnedDescription = data.GetProperty("description").GetString()!;
        await Assert.That(returnedDescription).IsEqualTo("Original description");

        string returnedLocation = data.GetProperty("location").GetString()!;
        await Assert.That(returnedLocation).IsEqualTo("Original location");

        // Verify original description and location are preserved in the database
        Machine? dbMachine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        await Assert.That(dbMachine).IsNotNull();
        await Assert.That(dbMachine!.Description).IsEqualTo("Original description");
        await Assert.That(dbMachine.Location).IsEqualTo("Original location");
    }

    [Test]
    public async Task UpdateMachine_ClearsNullableFields()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        // Send null for description and location to clear them
        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = "Still Named", description = (string?)null, location = (string?)null });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        // Verify response has null or missing values for description and location
        JsonValueKind descKind = data.GetProperty("description").ValueKind;
        await Assert.That(descKind).IsEqualTo(JsonValueKind.Null);

        JsonValueKind locKind = data.GetProperty("location").ValueKind;
        await Assert.That(locKind).IsEqualTo(JsonValueKind.Null);

        // Verify database row was cleared
        Machine? dbMachine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        await Assert.That(dbMachine).IsNotNull();
        await Assert.That(dbMachine!.Description).IsNull();
        await Assert.That(dbMachine.Location).IsNull();
    }

    [Test]
    public async Task UpdateMachine_MaxLengthName_Succeeds()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        string maxName = new string('A', 250);
        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = maxName, description = "desc", location = "loc" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        string returnedName = data.GetProperty("name").GetString()!;
        await Assert.That(returnedName).IsEqualTo(maxName);
    }

    [Test]
    public async Task UpdateMachine_MaxLengthLocation_Succeeds()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        string maxLocation = new string('L', 250);
        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = "Some Name", description = "desc", location = maxLocation });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        string returnedLocation = data.GetProperty("location").GetString()!;
        await Assert.That(returnedLocation).IsEqualTo(maxLocation);
    }

    [Test]
    public async Task UpdateMachine_CreatesAuditLog()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = "Audited Update", description = "Audit desc", location = "Audit loc" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Verify an audit log entry was created with MachineUpdated (22)
        AuditLogEntry? auditEntry = await db.AuditLog
            .FirstOrDefaultAsync(a => a.MachineId == machineId &&
                                      a.Action == AuditAction.MachineUpdated);

        await Assert.That(auditEntry).IsNotNull();
        await Assert.That(auditEntry!.TenantId).IsEqualTo(tenantId);
        await Assert.That(auditEntry.UserId).IsEqualTo(userId);
        await Assert.That(auditEntry.MachineId).IsEqualTo(machineId);
    }

    // ──────────────────────────────────────────────
    // Authorization & tenant isolation
    // ──────────────────────────────────────────────

    [Test]
    public async Task UpdateMachine_ViewerRole_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = "Should Fail", description = "nope", location = "nope" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task UpdateMachine_MachineAdminRole_Succeeds()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = "MachineAdmin Update", description = "ok", location = "ok" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        bool success = doc.RootElement.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();
    }

    [Test]
    public async Task UpdateMachine_TenantAdminRole_Succeeds()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = "TenantAdmin Update", description = "ok", location = "ok" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        bool success = doc.RootElement.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();
    }

    [Test]
    public async Task UpdateMachine_CrossTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int _, long machineId1, string _) = await SeedEnvironment(db);
        (int tenantId2, int userId2, long _, string _) = await SeedEnvironment(db);

        // User from tenant 2 tries to update machine from tenant 1
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId2)
            .WithRole(tenantId2, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId2)
            .Build();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId1}",
            new { name = "Cross Tenant Attack", description = "hacked", location = "hacked" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // Verify the machine was NOT modified in the database
        Machine? machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId1);
        await Assert.That(machine).IsNotNull();
        await Assert.That(machine!.Name).IsNotEqualTo("Cross Tenant Attack");
    }

    // ──────────────────────────────────────────────
    // Validation & edge cases
    // ──────────────────────────────────────────────

    [Test]
    public async Task UpdateMachine_EmptyName_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = "", description = "desc", location = "loc" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task UpdateMachine_WhitespaceOnlyName_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = "   ", description = "desc", location = "loc" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task UpdateMachine_NameExceeds250Chars_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        string tooLongName = new string('N', 251);
        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = tooLongName, description = "desc", location = "loc" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task UpdateMachine_LocationExceeds250Chars_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        string tooLongLocation = new string('L', 251);
        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{machineId}",
            new { name = "Valid Name", description = "desc", location = tooLongLocation });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task UpdateMachine_NonExistentMachine_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _, string _) = await SeedEnvironment(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            "/api/v1/machines/99999",
            new { name = "Ghost Machine", description = "desc", location = "loc" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpdateMachine_DeletedMachine_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long _, string _) = await SeedEnvironment(db);

        // Seed a soft-deleted machine
        Machine deletedMachine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = "Deleted Machine",
            Description = "Was deleted",
            Location = "Gone",
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = true,
            DeletedOn = DateTimeOffset.UtcNow,
            TenantId = tenantId,
        };
        deletedMachine.Id = await db.InsertWithInt64IdentityAsync(deletedMachine);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            $"/api/v1/machines/{deletedMachine.Id}",
            new { name = "Revive Attempt", description = "nope", location = "nope" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpdateMachine_NoAuthToken_Returns401()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            "/api/v1/machines/1",
            new { name = "No Auth", description = "desc", location = "loc" });

        bool isRejected = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                          (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isRejected).IsTrue();
    }

    // ──────────────────────────────────────────────
    // Hostname fix tests
    // ──────────────────────────────────────────────

    [Test]
    public async Task GetMachine_WithTelemetryHostname_ReturnsHostnameFromSummary()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string machineName) =
            await SeedEnvironmentWithHostname(db, "real-telemetry-host.example.com");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        // Hostname should come from MachineStateSummary, not Machine.Name
        string hostname = data.GetProperty("hostname").GetString()!;
        await Assert.That(hostname).IsEqualTo("real-telemetry-host.example.com");

        // Name should still be the machine name, not the hostname
        string name = data.GetProperty("name").GetString()!;
        await Assert.That(name).IsEqualTo(machineName);
    }

    [Test]
    public async Task GetMachine_NoTelemetryYet_FallsBackToName()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string machineName) =
            await SeedEnvironmentWithHostname(db, null);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        // When no telemetry hostname exists, hostname should fall back to machine name
        string hostname = data.GetProperty("hostname").GetString()!;
        await Assert.That(hostname).IsEqualTo(machineName);
    }

    [Test]
    public async Task ListMachines_HostnameReflectsTelemetry()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId, string _) =
            await SeedEnvironmentWithHostname(db, "list-telemetry-host.example.com");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        JsonElement items = data.GetProperty("items");

        // Find the machine we seeded by its ID
        bool foundWithCorrectHostname = false;
        foreach (JsonElement item in items.EnumerateArray())
        {
            long itemId = item.GetProperty("id").GetInt64();
            if (itemId == machineId)
            {
                string hostname = item.GetProperty("hostname").GetString()!;
                await Assert.That(hostname).IsEqualTo("list-telemetry-host.example.com");
                foundWithCorrectHostname = true;
            }
        }

        await Assert.That(foundWithCorrectHostname).IsTrue();
    }
}
