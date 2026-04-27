// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using System.Net.Http.Json;
using System.Net;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for webhook CRUD endpoints.
/// </summary>
public sealed class WebhookEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedWebhookEnvironment(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Webhook Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            MachineLimit = tier == SubscriptionTier.Free ? 3 : null,
            RetentionDays = 30,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-webhook-user-{Guid.NewGuid():N}",
            Username = $"webhookuser-{Guid.NewGuid():N}@example.com",
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

    private static HttpClient BuildClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // --- CreateWebhook Tests ---

    [Test]
    public async Task CreateWebhook_NoTenant_Returns403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Test",
            Url = "https://hooks.example.com/test",
        });

        // The TenantAdmin policy rejects users without a matching tenant role
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task CreateWebhook_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Test",
            Url = "https://hooks.example.com/test",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task CreateWebhook_ValidRequest_ReturnsDtoWithoutSecret()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "My Webhook",
            Url = "https://hooks.example.com/alerts",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("My Webhook");
        await Assert.That(body).Contains("https://hooks.example.com/alerts");
        // The DTO should not contain the secret
        await Assert.That(body.Contains("\"secret\"")).IsFalse();
    }

    [Test]
    public async Task CreateWebhook_StoresSecretInDatabase()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Secret Check",
            Url = "https://hooks.example.com/secret",
        });

        WebhookEndpoint? webhook = await db.WebhookEndpoints.FirstOrDefaultAsync(w => w.Name == "Secret Check");
        await Assert.That(webhook).IsNotNull();
        await Assert.That(webhook!.Secret.Length).IsEqualTo(64);
    }

    [Test]
    public async Task CreateWebhook_SetsCreatedByUserId()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Creator Check",
            Url = "https://hooks.example.com/creator",
        });

        WebhookEndpoint? webhook = await db.WebhookEndpoints.FirstOrDefaultAsync(w => w.Name == "Creator Check");
        await Assert.That(webhook).IsNotNull();
        await Assert.That(webhook!.CreatedByUserId).IsEqualTo(userId);
    }

    // --- ListWebhooks Tests ---

    [Test]
    public async Task ListWebhooks_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/webhooks");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task ListWebhooks_Empty_ReturnsEmptyList()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/webhooks");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("\"data\":[]");
    }

    [Test]
    public async Task ListWebhooks_MultipleWebhooks_SortedByName()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);

        WebhookEndpoint zeta = new()
        {
            TenantId = tenantId,
            Name = "Zeta Hook",
            Url = "https://hooks.example.com/zeta",
            Secret = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
            IsEnabled = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(zeta);

        WebhookEndpoint alpha = new()
        {
            TenantId = tenantId,
            Name = "Alpha Hook",
            Url = "https://hooks.example.com/alpha",
            Secret = "b1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
            IsEnabled = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(alpha);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/webhooks");

        string body = await response.Content.ReadAsStringAsync();
        int alphaIndex = body.IndexOf("Alpha Hook", StringComparison.Ordinal);
        int zetaIndex = body.IndexOf("Zeta Hook", StringComparison.Ordinal);
        await Assert.That(alphaIndex < zetaIndex).IsTrue();
    }

    // --- DeleteWebhook Tests ---

    [Test]
    public async Task DeleteWebhook_NonexistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/webhooks/99999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteWebhook_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1) = await SeedWebhookEnvironment(db);
        (int tenantId2, int userId2) = await SeedWebhookEnvironment(db);

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId1,
            Name = "Tenant1 Hook",
            Url = "https://hooks.example.com/t1",
            Secret = "c1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
            IsEnabled = true,
            CreatedByUserId = userId1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        webhook.Id = await db.InsertWithInt32IdentityAsync(webhook);

        HttpClient client = BuildClient(factory, tenantId2, userId2);

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/webhooks/{webhook.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteWebhook_Exists_DeletesSuccessfully()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Delete Me",
            Url = "https://hooks.example.com/delete",
            Secret = "d1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
            IsEnabled = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        webhook.Id = await db.InsertWithInt32IdentityAsync(webhook);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/webhooks/{webhook.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        WebhookEndpoint? deleted = await db.WebhookEndpoints.FirstOrDefaultAsync(w => w.Id == webhook.Id);
        await Assert.That(deleted).IsNull();
    }
}
