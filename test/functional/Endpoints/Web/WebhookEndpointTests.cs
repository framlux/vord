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
    public async Task CreateWebhook_ValidRequest_ReturnsDtoWithSecret()
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
        // The create response now includes the secret for one-time reveal
        await Assert.That(body.Contains("\"secret\"")).IsTrue();
    }

    [Test]
    public async Task CreateWebhook_StoresEncryptedSecretInDatabase()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Secret Check",
            Url = "https://hooks.example.com/secret",
        });

        string responseBody = await response.Content.ReadAsStringAsync();
        System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(responseBody);
        string plaintextSecret = doc.RootElement.GetProperty("data").GetProperty("secret").GetString()!;

        WebhookEndpoint? webhook = await db.WebhookEndpoints.FirstOrDefaultAsync(w => w.Name == "Secret Check");
        await Assert.That(webhook).IsNotNull();
        // The stored secret should be encrypted and differ from the plaintext returned in the response
        await Assert.That(webhook!.Secret).IsNotEqualTo(plaintextSecret);
        await Assert.That(string.IsNullOrEmpty(webhook.Secret)).IsFalse();
        // The plaintext secret should be a 64-character hex string
        await Assert.That(plaintextSecret.Length).IsEqualTo(64);
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

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();

        System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
        System.Text.Json.JsonElement dataArray = doc.RootElement.GetProperty("data");
        await Assert.That(dataArray.GetArrayLength()).IsGreaterThanOrEqualTo(2);
        await Assert.That(dataArray[0].GetProperty("name").GetString()).IsEqualTo("Alpha Hook");
        await Assert.That(dataArray[1].GetProperty("name").GetString()).IsEqualTo("Zeta Hook");
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

    // --- WS-5: Webhook Secret & Signature Tests ---

    [Test]
    public async Task CreateWebhook_ResponseIncludesSecret()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Secret Reveal",
            Url = "https://hooks.example.com/secret-reveal",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"secret\":");

        // The secret should be a 64-character hex string (32 bytes)
        System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
        string? secret = doc.RootElement.GetProperty("data").GetProperty("secret").GetString();
        await Assert.That(secret).IsNotNull();
        await Assert.That(secret!.Length).IsEqualTo(64);
        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(secret, "^[a-f0-9]{64}$")).IsTrue();
    }

    [Test]
    public async Task CreateWebhook_SecretIsUnique()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response1 = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Unique Secret 1",
            Url = "https://hooks.example.com/unique1",
        });
        HttpResponseMessage response2 = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Unique Secret 2",
            Url = "https://hooks.example.com/unique2",
        });

        string body1 = await response1.Content.ReadAsStringAsync();
        string body2 = await response2.Content.ReadAsStringAsync();

        System.Text.Json.JsonDocument doc1 = System.Text.Json.JsonDocument.Parse(body1);
        System.Text.Json.JsonDocument doc2 = System.Text.Json.JsonDocument.Parse(body2);
        string? secret1 = doc1.RootElement.GetProperty("data").GetProperty("secret").GetString();
        string? secret2 = doc2.RootElement.GetProperty("data").GetProperty("secret").GetString();

        await Assert.That(secret1).IsNotNull();
        await Assert.That(secret2).IsNotNull();
        await Assert.That(secret1).IsNotEqualTo(secret2);
    }

    [Test]
    public async Task ListWebhooks_DoesNotIncludeSecret()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        // Create a webhook first
        await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "No Secret in List",
            Url = "https://hooks.example.com/nosecret",
        });

        // List webhooks and verify secret is null
        HttpResponseMessage listResponse = await client.GetAsync("/api/v1/webhooks");
        await Assert.That(listResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string listBody = await listResponse.Content.ReadAsStringAsync();
        await Assert.That(listBody).Contains("No Secret in List");

        // The secret field should be null in list responses
        System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(listBody);
        System.Text.Json.JsonElement dataArray = doc.RootElement.GetProperty("data");
        System.Text.Json.JsonElement firstItem = dataArray[0];
        System.Text.Json.JsonElement secretElement = firstItem.GetProperty("secret");
        await Assert.That(secretElement.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
    }

    [Test]
    public async Task RotateSecret_ValidWebhook_ReturnsNewSecret()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        // Create a webhook and capture the original secret
        HttpResponseMessage createResponse = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Rotate Target",
            Url = "https://hooks.example.com/rotate",
        });
        string createBody = await createResponse.Content.ReadAsStringAsync();
        System.Text.Json.JsonDocument createDoc = System.Text.Json.JsonDocument.Parse(createBody);
        string? originalSecret = createDoc.RootElement.GetProperty("data").GetProperty("secret").GetString();
        int webhookId = createDoc.RootElement.GetProperty("data").GetProperty("id").GetInt32();

        // Rotate the secret
        HttpResponseMessage rotateResponse = await client.PostAsync($"/api/v1/webhooks/{webhookId}/rotate-secret", null);
        await Assert.That(rotateResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string rotateBody = await rotateResponse.Content.ReadAsStringAsync();
        System.Text.Json.JsonDocument rotateDoc = System.Text.Json.JsonDocument.Parse(rotateBody);
        string? newSecret = rotateDoc.RootElement.GetProperty("data").GetProperty("secret").GetString();

        await Assert.That(newSecret).IsNotNull();
        await Assert.That(newSecret!.Length).IsEqualTo(64);
        await Assert.That(newSecret).IsNotEqualTo(originalSecret);
    }

    [Test]
    public async Task RotateSecret_NonExistentWebhook_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/webhooks/99999/rotate-secret", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task RotateSecret_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1) = await SeedWebhookEnvironment(db);
        (int tenantId2, int userId2) = await SeedWebhookEnvironment(db);

        // Create webhook in tenant 1
        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId1,
            Name = "Tenant1 Rotate Hook",
            Url = "https://hooks.example.com/t1rotate",
            Secret = "e1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
            IsEnabled = true,
            CreatedByUserId = userId1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        webhook.Id = await db.InsertWithInt32IdentityAsync(webhook);

        // Authenticate as tenant 2 and try to rotate
        HttpClient client = BuildClient(factory, tenantId2, userId2);
        HttpResponseMessage response = await client.PostAsync($"/api/v1/webhooks/{webhook.Id}/rotate-secret", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task RotateSecret_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/webhooks/1/rotate-secret", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task RotateSecret_ViewerRole_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);

        // Build client with Viewer role instead of TenantAdmin
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/webhooks/1/rotate-secret", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // --- Cross-Cutting Webhook Validation Tests ---

    [Test]
    public async Task CreateWebhook_HttpUrl_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "HTTP Hook",
            Url = "http://hooks.example.com/test",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.ToLowerInvariant()).Contains("https");
    }

    [Test]
    public async Task CreateWebhook_PrivateIpUrl_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Private IP Hook",
            Url = "https://192.168.1.1/alert",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.ToLowerInvariant()).Contains("private");
    }

    [Test]
    public async Task CreateWebhook_EmptyName_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "",
            Url = "https://hooks.example.com/test",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CreateWebhook_EmptyUrl_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "No URL Hook",
            Url = "",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // --- WS-7: Webhook Update (Enable/Disable Toggle) Tests ---

    [Test]
    public async Task UpdateWebhook_ToggleEnabled_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Toggle Hook",
            Url = "https://hooks.example.com/toggle",
            Secret = "f1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
            IsEnabled = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        webhook.Id = await db.InsertWithInt32IdentityAsync(webhook);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/webhooks/{webhook.Id}", new
        {
            IsEnabled = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("\"isEnabled\":false");
    }

    [Test]
    public async Task UpdateWebhook_ToggleEnabled_PersistsInList()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Persist Toggle Hook",
            Url = "https://hooks.example.com/persist-toggle",
            Secret = "g1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
            IsEnabled = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        webhook.Id = await db.InsertWithInt32IdentityAsync(webhook);

        HttpClient client = BuildClient(factory, tenantId, userId);

        // Toggle off
        await client.PutAsJsonAsync($"/api/v1/webhooks/{webhook.Id}", new { IsEnabled = false });

        // Verify in list
        HttpResponseMessage listResponse = await client.GetAsync("/api/v1/webhooks");
        string listBody = await listResponse.Content.ReadAsStringAsync();
        await Assert.That(listBody).Contains("Persist Toggle Hook");
        await Assert.That(listBody).Contains("\"isEnabled\":false");
    }

    [Test]
    public async Task UpdateWebhook_NonExistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/webhooks/99999", new
        {
            IsEnabled = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpdateWebhook_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1) = await SeedWebhookEnvironment(db);
        (int tenantId2, int userId2) = await SeedWebhookEnvironment(db);

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId1,
            Name = "Tenant1 Update Hook",
            Url = "https://hooks.example.com/t1update",
            Secret = "h1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
            IsEnabled = true,
            CreatedByUserId = userId1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        webhook.Id = await db.InsertWithInt32IdentityAsync(webhook);

        HttpClient client = BuildClient(factory, tenantId2, userId2);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/webhooks/{webhook.Id}", new
        {
            IsEnabled = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpdateWebhook_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/webhooks/1", new
        {
            IsEnabled = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task UpdateWebhook_ViewerRole_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);

        // Build client with Viewer role instead of TenantAdmin
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/webhooks/1", new
        {
            IsEnabled = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // --- Limit, Validation, and Canceled Subscription Tests ---

    [Test]
    public async Task CreateWebhook_AtLimit_Returns403WithLimitMessage()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);

        // Set the webhook limit to 1 so we can hit it with a single webhook
        await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.WebhookLimit, 1)
            .UpdateAsync();

        HttpClient client = BuildClient(factory, tenantId, userId);

        // Create first webhook to consume the limit
        HttpResponseMessage firstResponse = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "First Webhook",
            Url = "https://hooks.example.com/first",
        });
        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Second webhook should be rejected because the limit is reached
        HttpResponseMessage secondResponse = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Second Webhook",
            Url = "https://hooks.example.com/second",
        });

        await Assert.That(secondResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await secondResponse.Content.ReadAsStringAsync();
        await Assert.That(body.ToLowerInvariant()).Contains("limit");
    }

    [Test]
    public async Task CreateWebhook_NameTooLong_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        string longName = new string('W', 251);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = longName,
            Url = "https://hooks.example.com/test",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("250 characters");
    }

    [Test]
    public async Task CreateWebhook_UrlTooLong_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        // Build a valid HTTPS URL that exceeds 2000 characters
        string longPath = new string('x', 1975);
        string longUrl = $"https://hooks.example.com/{longPath}";

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Long URL Webhook",
            Url = longUrl,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("2000 characters");
    }

    [Test]
    public async Task CreateWebhook_CanceledSubscription_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedWebhookEnvironment(db);

        // Mark the subscription as canceled to simulate a lapsed subscription
        await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Status, SubscriptionStatus.Canceled)
            .UpdateAsync();

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Name = "Should Fail",
            Url = "https://hooks.example.com/canceled",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.ToLowerInvariant()).Contains("canceled");
    }
}
