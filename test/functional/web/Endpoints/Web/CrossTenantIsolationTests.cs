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

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests verifying cross-tenant isolation through the full HTTP pipeline.
/// Ensures that tenant ID extraction from cookies and handler-level filtering work together.
/// </summary>
public sealed class CrossTenantIsolationTests
{
    [Test]
    public async Task MachineList_OnlyShowsCurrentTenantMachines()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenant1Id = await SeedTenantWithSubscription(db, "Tenant 1");
        int tenant2Id = await SeedTenantWithSubscription(db, "Tenant 2");
        await SeedMachine(db, tenant1Id, "tenant1-web");
        await SeedMachine(db, tenant1Id, "tenant1-db");
        await SeedMachine(db, tenant2Id, "tenant2-web");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenant1Id, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenant1Id)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        JsonElement data = root.GetProperty("data");
        int totalCount = data.GetProperty("totalCount").GetInt32();
        await Assert.That(totalCount).IsEqualTo(2);

        JsonElement items = data.GetProperty("items");
        int itemCount = items.GetArrayLength();
        await Assert.That(itemCount).IsEqualTo(2);

        // Collect all machine names from the response
        List<string> machineNames = new();
        foreach (JsonElement item in items.EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            machineNames.Add(name);
        }

        // Verify tenant 1 machines are present
        bool containsTenant1Web = machineNames.Contains("tenant1-web");
        await Assert.That(containsTenant1Web).IsTrue();

        bool containsTenant1Db = machineNames.Contains("tenant1-db");
        await Assert.That(containsTenant1Db).IsTrue();

        // Verify tenant 2 machines are NOT present (isolation check)
        bool containsTenant2Web = machineNames.Contains("tenant2-web");
        await Assert.That(containsTenant2Web).IsFalse();
    }

    [Test]
    public async Task MachineDetail_CrossTenantAccess_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenant1Id = await SeedTenantWithSubscription(db, "Tenant 1");
        int tenant2Id = await SeedTenantWithSubscription(db, "Tenant 2");
        long tenant2MachineId = await SeedMachine(db, tenant2Id, "secret-machine");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenant1Id, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenant1Id)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{tenant2MachineId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // Verify the response does not leak any machine data from the other tenant
        string body = await response.Content.ReadAsStringAsync();
        bool containsMachineName = body.Contains("secret-machine");
        await Assert.That(containsMachineName).IsFalse();
    }

    [Test]
    public async Task MachineDelete_CrossTenantAccess_FailsAndPreservesMachine()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenant1Id = await SeedTenantWithSubscription(db, "Tenant 1");
        int tenant2Id = await SeedTenantWithSubscription(db, "Tenant 2");
        long tenant2MachineId = await SeedMachine(db, tenant2Id, "protected-machine");

        // Seed a user with MachineAdmin on tenant 1 only
        UserAccount user = new()
        {
            ExternalId = $"ext-isolation-{Guid.NewGuid():N}",
            Username = $"isolation-{Guid.NewGuid():N}@example.com",
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
            AssignedTenantId = tenant1Id,
            Role = UserAccountRoles.MachineAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithRole(tenant1Id, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenant1Id)
            .Build();

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/machines/{tenant2MachineId}");

        // Cross-tenant deletion must fail
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // Verify the machine was NOT deleted in the database
        Machine? machine = await db.Machines.Where(m => m.Id == tenant2MachineId).FirstOrDefaultAsync();
        await Assert.That(machine).IsNotNull();
        await Assert.That(machine!.IsDeleted).IsFalse();
    }

    [Test]
    public async Task TenantSwitch_ToNonMemberTenant_Returns403WithErrorPayload()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenant1Id = await SeedTenantWithSubscription(db, "Tenant 1");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenant1Id, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenant1Id)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/tenants/switch", new { TenantId = 999 });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString()!;
        await Assert.That(message).Contains("You do not have access to this tenant");
    }

    [Test]
    public async Task DashboardSummary_OnlyIncludesCurrentTenantData()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenant1Id = await SeedTenantWithSubscription(db, "Tenant 1");
        int tenant2Id = await SeedTenantWithSubscription(db, "Tenant 2");
        await SeedMachine(db, tenant1Id, "t1-host-1");
        await SeedMachine(db, tenant1Id, "t1-host-2");
        await SeedMachine(db, tenant2Id, "t2-host-1");
        await SeedMachine(db, tenant2Id, "t2-host-2");
        await SeedMachine(db, tenant2Id, "t2-host-3");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenant1Id, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenant1Id)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/summary");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        JsonElement data = root.GetProperty("data");
        int totalMachines = data.GetProperty("totalMachines").GetInt32();
        await Assert.That(totalMachines).IsEqualTo(2);

        // Verify the count does NOT include tenant 2's 3 machines
        bool countsAreIsolated = totalMachines < 5;
        await Assert.That(countsAreIsolated).IsTrue();
    }

    [Test]
    public async Task DashboardSummary_CrossTenantVerification_EachTenantSeesOnlyOwnCount()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenant1Id = await SeedTenantWithSubscription(db, "Tenant A");
        int tenant2Id = await SeedTenantWithSubscription(db, "Tenant B");
        await SeedMachine(db, tenant1Id, "a-host-1");
        await SeedMachine(db, tenant2Id, "b-host-1");
        await SeedMachine(db, tenant2Id, "b-host-2");
        await SeedMachine(db, tenant2Id, "b-host-3");

        // Query dashboard as tenant 2 user
        HttpClient client2 = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenant2Id, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenant2Id)
            .Build();

        HttpResponseMessage response2 = await client2.GetAsync("/api/v1/dashboard/summary");

        await Assert.That(response2.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body2 = await response2.Content.ReadAsStringAsync();
        using JsonDocument doc2 = JsonDocument.Parse(body2);
        JsonElement data2 = doc2.RootElement.GetProperty("data");
        int tenant2Machines = data2.GetProperty("totalMachines").GetInt32();
        await Assert.That(tenant2Machines).IsEqualTo(3);

        // Query dashboard as tenant 1 user to verify isolation in the other direction
        HttpClient client1 = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenant1Id, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenant1Id)
            .Build();

        HttpResponseMessage response1 = await client1.GetAsync("/api/v1/dashboard/summary");

        await Assert.That(response1.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body1 = await response1.Content.ReadAsStringAsync();
        using JsonDocument doc1 = JsonDocument.Parse(body1);
        JsonElement data1 = doc1.RootElement.GetProperty("data");
        int tenant1Machines = data1.GetProperty("totalMachines").GetInt32();
        await Assert.That(tenant1Machines).IsEqualTo(1);
    }

    [Test]
    public async Task RegistrationTokens_IsolatedByTenant()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenant1Id = await SeedTenantWithSubscription(db, "Tenant 1");
        int tenant2Id = await SeedTenantWithSubscription(db, "Tenant 2");

        // Seed tokens for tenant 2
        await db.InsertAsync(new RegistrationToken
        {
            TenantId = tenant2Id,
            TokenHash = "hash-t2-1",
            Name = "Tenant2 Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false,
        });

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenant1Id, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenant1Id)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/registration-tokens");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        JsonElement data = root.GetProperty("data");
        int totalCount = data.GetProperty("totalCount").GetInt32();
        await Assert.That(totalCount).IsEqualTo(0);

        // Verify tenant 2 token name does not leak into the response
        bool containsTenant2Token = body.Contains("Tenant2 Token");
        await Assert.That(containsTenant2Token).IsFalse();
    }

    private static async Task<int> SeedTenantWithSubscription(DatabaseContext db, string name)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = name,
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
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        return tenant.Id;
    }

    private static async Task<long> SeedMachine(DatabaseContext db, int tenantId, string hostname)
    {
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = hostname,
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        return machine.Id;
    }
}
