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

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web.Tenants;

/// <summary>
/// Verifies that a deactivated tenant (Phase-1 tenant deletion, <c>Tenants.IsActive=false</c>)
/// stops granting web access: its roles drop out of <c>/auth/me</c>, switching into it is
/// rejected without setting the active-tenant cookie, active tenants are unaffected, and a
/// global admin is unaffected either way.
/// </summary>
public sealed class TenantDeactivationAccessTests
{
    [Test]
    public async Task AuthMe_UserInActiveAndDeactivatedTenant_ReturnsOnlyActiveTenant()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int activeTenantId = await SeedTenantWithSubscription(db, "Tenant Active", isActive: true);
        int deactivatedTenantId = await SeedTenantWithSubscription(db, "Tenant Deactivated", isActive: false);

        UserAccount user = TestDataBuilder.BuildUser();
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        await db.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: user.Id, tenantId: activeTenantId, assignedByUserId: user.Id));
        await db.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: user.Id, tenantId: deactivatedTenantId, assignedByUserId: user.Id));

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId(user.ExternalId)
            .WithEmail(user.Username)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/me");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement tenants = doc.RootElement.GetProperty("data").GetProperty("tenants");

        await Assert.That(tenants.GetArrayLength()).IsEqualTo(1);
        await Assert.That(tenants[0].GetProperty("tenantId").GetInt32()).IsEqualTo(activeTenantId);
    }

    [Test]
    public async Task TenantSwitch_ToDeactivatedTenant_Returns403AndDoesNotSetCookie()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int activeTenantId = await SeedTenantWithSubscription(db, "Tenant Active", isActive: true);
        int deactivatedTenantId = await SeedTenantWithSubscription(db, "Tenant Deactivated", isActive: false);

        await db.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: 1, tenantId: activeTenantId, assignedByUserId: 1));
        await db.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: 1, tenantId: deactivatedTenantId, assignedByUserId: 1));

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(activeTenantId, (int)UserAccountRoles.TenantAdmin)
            .WithRole(deactivatedTenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(activeTenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/tenants/switch", new { TenantId = deactivatedTenantId });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(root.GetProperty("message").GetString()).Contains("You do not have access to this tenant");

        bool setCookieCarriesDeactivatedTenant = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies)
            && cookies.Any(c => c.StartsWith($"vord_tenant={deactivatedTenantId}"));
        await Assert.That(setCookieCarriesDeactivatedTenant).IsFalse();
    }

    [Test]
    public async Task TenantSwitch_BetweenTwoActiveTenants_StillSucceedsForBoth()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantAId = await SeedTenantWithSubscription(db, "Tenant A", isActive: true);
        int tenantBId = await SeedTenantWithSubscription(db, "Tenant B", isActive: true);

        await db.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: 1, tenantId: tenantAId, assignedByUserId: 1));
        await db.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: 1, tenantId: tenantBId, assignedByUserId: 1));

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantAId, (int)UserAccountRoles.TenantAdmin)
            .WithRole(tenantBId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantAId)
            .Build();

        HttpResponseMessage switchToB = await client.PostAsJsonAsync("/api/v1/tenants/switch", new { TenantId = tenantBId });
        await Assert.That(switchToB.StatusCode).IsEqualTo(HttpStatusCode.OK);

        bool switchToBSetCookie = switchToB.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? bCookies)
            && bCookies.Any(c => c.StartsWith($"vord_tenant={tenantBId}"));
        await Assert.That(switchToBSetCookie).IsTrue();

        HttpResponseMessage switchToA = await client.PostAsJsonAsync("/api/v1/tenants/switch", new { TenantId = tenantAId });
        await Assert.That(switchToA.StatusCode).IsEqualTo(HttpStatusCode.OK);

        bool switchToASetCookie = switchToA.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? aCookies)
            && aCookies.Any(c => c.StartsWith($"vord_tenant={tenantAId}"));
        await Assert.That(switchToASetCookie).IsTrue();
    }

    [Test]
    public async Task GlobalAdmin_TenantScopedEndpoint_UnaffectedByDeactivatedTenant()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int deactivatedTenantId = await SeedTenantWithSubscription(db, "Tenant Deactivated", isActive: false);
        await SeedMachine(db, deactivatedTenantId, "admin-visible-host");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .AsGlobalAdmin()
            .WithRole(deactivatedTenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(deactivatedTenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        int totalCount = doc.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32();
        await Assert.That(totalCount).IsEqualTo(1);
    }

    private static async Task<int> SeedTenantWithSubscription(DatabaseContext db, string name, bool isActive)
    {
        Tenant tenant = TestDataBuilder.BuildTenant(name: name, isActive: isActive);
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
        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: hostname);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        return machine.Id;
    }
}
