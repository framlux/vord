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
/// Functional tests for the ReactivateSubscriptionEndpoint.
/// </summary>
public sealed class ReactivateSubscriptionEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedBillingEnvironment(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro,
        SubscriptionStatus status = SubscriptionStatus.Active)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Billing Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = tier,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-{Guid.NewGuid():N}",
            Username = $"user-{Guid.NewGuid():N}@example.com",
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
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        return (tenant.Id, user.Id);
    }

    private static HttpClient BuildClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    [Test]
    public async Task ReactivateCanceledSubscription_Returns200_RevertsToFreeTier()
    {
        // A fully canceled subscription should be reactivated on the Free tier
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(
            db,
            status: SubscriptionStatus.Canceled);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/reactivate", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsTrue();

        JsonElement data = root.GetProperty("data");
        bool dataSuccess = data.GetProperty("success").GetBoolean();
        await Assert.That(dataSuccess).IsTrue();

        string message = data.GetProperty("message").GetString()!;
        await Assert.That(message).Contains("reactivated on the Free tier");
    }

    [Test]
    public async Task ActiveSubscription_Returns400()
    {
        // An active subscription cannot be reactivated since it is not canceled
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(
            db,
            status: SubscriptionStatus.Active);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/reactivate", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsFalse();

        string errorMessage = root.GetProperty("message").GetString()!;
        await Assert.That(errorMessage).Contains("Subscription is not canceled");
    }

    [Test]
    public async Task PastDueSubscription_Returns400()
    {
        // A past-due subscription is not in the canceled state and cannot be reactivated
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(
            db,
            status: SubscriptionStatus.PastDue);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/reactivate", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsFalse();

        string errorMessage = root.GetProperty("message").GetString()!;
        await Assert.That(errorMessage).Contains("Subscription is not canceled");
    }

    [Test]
    public async Task MissingSubscription_Returns404()
    {
        // A tenant with no subscription record should receive a 404 Not Found
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"No Sub Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        UserAccount user = new()
        {
            ExternalId = $"ext-{Guid.NewGuid():N}",
            Username = $"user-{Guid.NewGuid():N}@example.com",
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
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        HttpClient client = BuildClient(factory, tenant.Id, user.Id);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/reactivate", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task NoTenantClaim_Returns403()
    {
        // A user without an active tenant claim is rejected by the TenantAdmin policy
        // before the handler executes, resulting in a 403 Forbidden
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/reactivate", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task ReactivateCanceled_VerifiesDatabaseState()
    {
        // After reactivation the database row must reflect the Free tier limits and Active status
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(
            db,
            tier: SubscriptionTier.Pro,
            status: SubscriptionStatus.Canceled);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/reactivate", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using DatabaseContext verifyDb = factory.CreateDbContext();
        TenantSubscription? subscription = await verifyDb.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(subscription).IsNotNull();
        await Assert.That(subscription!.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(subscription.Status).IsEqualTo(SubscriptionStatus.Active);
    }
}
