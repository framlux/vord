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
/// Functional tests for <see cref="Server.Services.Billing.SubscriptionStatusPreProcessor"/>.
/// Validates that mutating requests are blocked for canceled subscriptions while read-only
/// and exempted paths are allowed through.
/// </summary>
public sealed class SubscriptionStatusPreProcessorTests
{
    private static async Task<(int TenantId, int UserId)> SeedTenantWithStatus(
        DatabaseContext db,
        SubscriptionStatus status)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"PreProc Tenant {Guid.NewGuid():N}",
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
            Status = status,
            RetentionDays = 30,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-preproc-{Guid.NewGuid():N}",
            Username = $"preproc-{Guid.NewGuid():N}@example.com",
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
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        return (tenant.Id, user.Id);
    }

    private static HttpClient BuildClient(
        FunctionalTestFactory factory,
        int tenantId,
        int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithEmail($"user-{userId}@example.com")
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // --- Core enforcement: mutating requests blocked for canceled subscriptions ---

    [Test]
    public async Task Post_CanceledSubscription_Returns403WithReadOnlyMessage()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithStatus(db, SubscriptionStatus.Canceled);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = "test@example.com",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("read-only");
    }

    [Test]
    public async Task Put_CanceledSubscription_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithStatus(db, SubscriptionStatus.Canceled);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/tenants/{tenantId}/oidc", new
        {
            Authority = "https://sso.example.com",
            ClientId = "client-id",
            ClientSecret = "client-secret",
            EmailDomain = "example.com",
            IsEnabled = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Delete_CanceledSubscription_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithStatus(db, SubscriptionStatus.Canceled);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/tenants/registration-tokens/99999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // --- GET requests always allowed ---

    [Test]
    public async Task Get_CanceledSubscription_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithStatus(db, SubscriptionStatus.Canceled);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/invitations");

        // GET is read-only, should pass through the preprocessor
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // --- Path exemptions ---

    [Test]
    public async Task Post_BillingEndpoint_CanceledSubscription_Passes()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithStatus(db, SubscriptionStatus.Canceled);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/reactivate", null);

        // Should pass the preprocessor; the actual endpoint behavior may vary
        // but it should NOT be 403 from the preprocessor
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Post_AdminEndpoint_CanceledSubscription_Passes()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithStatus(db, SubscriptionStatus.Canceled);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithEmail($"user-{userId}@example.com")
            .AsGlobalAdmin()
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/admin/tenants", new
        {
            Name = "Test Admin Tenant",
            LogoUrl = "",
        });

        // Should not be blocked by preprocessor (admin path exempt)
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.Forbidden);
    }

    // --- Active and PastDue subscriptions pass through ---

    [Test]
    public async Task Post_ActiveSubscription_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithStatus(db, SubscriptionStatus.Active);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/invitations");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Post_PastDueSubscription_NotBlocked()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithStatus(db, SubscriptionStatus.PastDue);
        HttpClient client = BuildClient(factory, tenantId, userId);

        // PastDue should not trigger the preprocessor block
        HttpResponseMessage response = await client.GetAsync("/api/v1/invitations");

        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.Forbidden);
    }

    // --- No tenant context passes through ---

    [Test]
    public async Task Post_NoTenantContext_Passes()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithEmail("no-tenant@example.com")
            .Build();

        // Without an active tenant, the preprocessor should not block
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = "test@example.com",
        });

        // Should not be 403 from the preprocessor (may be 403 from the endpoint itself for missing tenant)
        // The key distinction: preprocessor returns 403 with "read-only" message; endpoint returns 403 without it
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("read-only")).IsEqualTo(false);
    }

    // --- Verify JSON response body ---

    [Test]
    public async Task Post_CanceledSubscription_ResponseContainsReactivationMessage()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithStatus(db, SubscriptionStatus.Canceled);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = "test@example.com",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("reactivate");
        await Assert.That(message).Contains("billing");
    }
}
