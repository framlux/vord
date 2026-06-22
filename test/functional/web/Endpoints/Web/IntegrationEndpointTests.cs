// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for integration CRUD endpoints.
/// </summary>
public sealed class IntegrationEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedTenantAndUser(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Test Tenant {Guid.NewGuid():N}",
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
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
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
            IsGlobalAdmin = false
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        UserTenantRole role = new()
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };
        await db.InsertAsync(role);

        return (tenant.Id, user.Id);
    }

    private static HttpClient BuildClient(
        FunctionalTestFactory factory,
        int tenantId,
        int userId,
        UserAccountRoles clientRole = UserAccountRoles.TenantAdmin)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)clientRole)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static async Task<int> SeedIntegration(
        DatabaseContext db,
        int tenantId,
        int userId,
        IntegrationProvider provider = IntegrationProvider.Slack,
        string name = "Test Integration",
        bool isDeleted = false)
    {
        string configuration = provider switch
        {
            IntegrationProvider.Slack => "{\"webhookUrl\":\"https://hooks.slack.com/services/T123/B456/abc\"}",
            IntegrationProvider.Custom => "{\"url\":\"https://example.com/webhook\",\"secret\":\"encrypted\"}",
            IntegrationProvider.Discord => "{\"webhookUrl\":\"https://discord.com/api/webhooks/123/abc\"}",
            IntegrationProvider.MicrosoftTeams => "{\"webhookUrl\":\"https://outlook.webhook.office.com/webhookb2/test\"}",
            IntegrationProvider.PagerDuty => "{\"routingKey\":\"aabbccddee11223344556677aabbccdd\"}",
            _ => "{}"
        };

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = provider,
            Name = name,
            Configuration = configuration,
            IsEnabled = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            DeletedAt = isDeleted ? DateTimeOffset.UtcNow : null,
            DeletedByUserId = isDeleted ? userId : null
        };
        integration.Id = await db.InsertWithInt32IdentityAsync(integration);

        return integration.Id;
    }

    // --- Create Endpoint Tests ---

    [Test]
    public async Task Create_ValidSlackIntegration_Returns201WithDto()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "Slack",
            name = "My Slack Integration",
            configuration = new Dictionary<string, string>
            {
                ["webhookUrl"] = "https://hooks.slack.com/services/T123/B456/abc123"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();
        JsonElement data = root.GetProperty("data");
        await Assert.That(data.GetProperty("provider").GetString()).IsEqualTo("Slack");
        await Assert.That(data.GetProperty("name").GetString()).IsEqualTo("My Slack Integration");
        await Assert.That(data.GetProperty("isEnabled").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("id").GetInt32()).IsGreaterThan(0);
    }

    [Test]
    public async Task Create_CustomProvider_ReturnsSecret()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "Custom",
            name = "My Custom Webhook",
            configuration = new Dictionary<string, string>
            {
                ["url"] = "https://my-server.example.com/webhook"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        string? secret = data.GetProperty("secret").GetString();
        await Assert.That(secret).IsNotNull();
        await Assert.That(secret!.Length).IsEqualTo(64);
    }

    [Test]
    public async Task Create_AtWebhookLimit_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db, SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId);

        // Pro tier has webhook limit of 5; seed 5 integrations
        for (int i = 0; i < 5; i++)
        {
            await SeedIntegration(db, tenantId, userId, IntegrationProvider.Slack, $"Integration {i}");
        }

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "Slack",
            name = "Sixth Integration",
            configuration = new Dictionary<string, string>
            {
                ["webhookUrl"] = "https://hooks.slack.com/services/T999/B999/xyz"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("limit reached");
    }

    [Test]
    public async Task Create_InvalidSlackUrl_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "Slack",
            name = "Bad Slack",
            configuration = new Dictionary<string, string>
            {
                ["webhookUrl"] = "https://not-slack.com/invalid"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("hooks.slack.com/services/");
    }

    [Test]
    public async Task Create_ProviderNone_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "None",
            name = "Invalid Provider",
            configuration = new Dictionary<string, string>()
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("cannot be None");
    }

    [Test]
    public async Task Create_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "Slack",
            name = "Free Tier Slack",
            configuration = new Dictionary<string, string>
            {
                ["webhookUrl"] = "https://hooks.slack.com/services/T123/B456/abc"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Pro or Team subscription");
    }

    // --- List Endpoint Tests ---

    [Test]
    public async Task List_ReturnsOnlyCurrentTenantIntegrations()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1) = await SeedTenantAndUser(db);
        (int tenantId2, int userId2) = await SeedTenantAndUser(db);

        await SeedIntegration(db, tenantId1, userId1, IntegrationProvider.Slack, "Tenant1 Slack");
        await SeedIntegration(db, tenantId2, userId2, IntegrationProvider.Discord, "Tenant2 Discord");

        HttpClient client = BuildClient(factory, tenantId1, userId1);
        HttpResponseMessage response = await client.GetAsync("/api/v1/integrations");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(1);
        await Assert.That(data[0].GetProperty("name").GetString()).IsEqualTo("Tenant1 Slack");
    }

    [Test]
    public async Task List_ExcludesDeletedIntegrations()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        await SeedIntegration(db, tenantId, userId, IntegrationProvider.Slack, "Active Integration", isDeleted: false);
        await SeedIntegration(db, tenantId, userId, IntegrationProvider.Discord, "Deleted Integration", isDeleted: true);

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.GetAsync("/api/v1/integrations");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(1);
        await Assert.That(data[0].GetProperty("name").GetString()).IsEqualTo("Active Integration");
    }

    [Test]
    public async Task List_NoIntegrations_ReturnsEmptyArray()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.GetAsync("/api/v1/integrations");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(0);
    }

    // --- Get Endpoint Tests ---

    [Test]
    public async Task Get_ExistingIntegration_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        int integrationId = await SeedIntegration(db, tenantId, userId, IntegrationProvider.Slack, "My Slack");

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.GetAsync($"/api/v1/integrations/{integrationId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("id").GetInt32()).IsEqualTo(integrationId);
        await Assert.That(data.GetProperty("provider").GetString()).IsEqualTo("Slack");
        await Assert.That(data.GetProperty("name").GetString()).IsEqualTo("My Slack");
        await Assert.That(data.GetProperty("isEnabled").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Get_NonExistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.GetAsync("/api/v1/integrations/99999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Get_OtherTenantIntegration_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1) = await SeedTenantAndUser(db);
        (int tenantId2, int userId2) = await SeedTenantAndUser(db);
        int integrationId = await SeedIntegration(db, tenantId2, userId2, IntegrationProvider.Slack, "Other Tenant");

        HttpClient client = BuildClient(factory, tenantId1, userId1);
        HttpResponseMessage response = await client.GetAsync($"/api/v1/integrations/{integrationId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Get_DeletedIntegration_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        int integrationId = await SeedIntegration(db, tenantId, userId, IntegrationProvider.Slack, "Deleted", isDeleted: true);

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.GetAsync($"/api/v1/integrations/{integrationId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // --- Update Endpoint Tests ---

    [Test]
    public async Task Update_ToggleEnabled_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        int integrationId = await SeedIntegration(db, tenantId, userId, IntegrationProvider.Slack, "Toggle Test");

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/integrations/{integrationId}", new
        {
            isEnabled = false
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("isEnabled").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Update_ChangeName_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        int integrationId = await SeedIntegration(db, tenantId, userId, IntegrationProvider.Slack, "Old Name");

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/integrations/{integrationId}", new
        {
            name = "New Name"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("name").GetString()).IsEqualTo("New Name");
    }

    [Test]
    public async Task Update_DeletedIntegration_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        int integrationId = await SeedIntegration(db, tenantId, userId, IntegrationProvider.Slack, "Deleted", isDeleted: true);

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/integrations/{integrationId}", new
        {
            name = "Updated Name"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // --- Delete Endpoint Tests ---

    [Test]
    public async Task Delete_ExistingIntegration_Returns204()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        int integrationId = await SeedIntegration(db, tenantId, userId, IntegrationProvider.Slack, "To Delete");

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage deleteResponse = await client.DeleteAsync($"/api/v1/integrations/{integrationId}");

        await Assert.That(deleteResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // Verify subsequent GET returns 404
        HttpResponseMessage getResponse = await client.GetAsync($"/api/v1/integrations/{integrationId}");
        await Assert.That(getResponse.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Delete_NonExistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.DeleteAsync("/api/v1/integrations/99999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // --- Rotate Secret Endpoint Tests ---

    [Test]
    public async Task RotateSecret_CustomProvider_Returns200WithNewSecret()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        int integrationId = await SeedIntegration(db, tenantId, userId, IntegrationProvider.Custom, "Custom Hook");

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.PostAsync($"/api/v1/integrations/{integrationId}/rotate-secret", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        string? secret = data.GetProperty("secret").GetString();
        await Assert.That(secret).IsNotNull();
        await Assert.That(secret!.Length).IsEqualTo(64);
    }

    [Test]
    public async Task RotateSecret_SlackProvider_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        int integrationId = await SeedIntegration(db, tenantId, userId, IntegrationProvider.Slack, "Slack Hook");

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.PostAsync($"/api/v1/integrations/{integrationId}/rotate-secret", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Custom provider");
    }

    [Test]
    public async Task RotateSecret_NonExistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.PostAsync("/api/v1/integrations/99999/rotate-secret", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // --- Test Endpoint Tests ---

    [Test]
    public async Task Test_Integration_ReturnsResultDto()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        int integrationId = await SeedIntegration(db, tenantId, userId, IntegrationProvider.Slack, "Test Delivery");

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.PostAsync($"/api/v1/integrations/{integrationId}/test", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();
        JsonElement data = root.GetProperty("data");
        // The test endpoint will attempt to deliver; it may fail because no real server exists,
        // but it should still return a valid result DTO with success and message fields
        await Assert.That(data.TryGetProperty("success", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("message", out _)).IsTrue();
    }

    // --- Providers Endpoint Tests ---

    [Test]
    public async Task Providers_ReturnsAllProviders()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        // Providers endpoint uses ViewOnly policy, so even Viewer role can access
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);
        HttpResponseMessage response = await client.GetAsync("/api/v1/integrations/providers");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(5);

        // Verify all provider names are present
        List<string> providerNames = new();
        for (int i = 0; i < data.GetArrayLength(); i++)
        {
            string? providerName = data[i].GetProperty("provider").GetString();
            if (providerName is not null)
            {
                providerNames.Add(providerName);
            }
        }

        await Assert.That(providerNames).Contains("Slack");
        await Assert.That(providerNames).Contains("MicrosoftTeams");
        await Assert.That(providerNames).Contains("Discord");
        await Assert.That(providerNames).Contains("PagerDuty");
        await Assert.That(providerNames).Contains("Custom");
    }

    // --- Auth Tests ---

    [Test]
    public async Task Create_ViewerRole_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "Slack",
            name = "Viewer Attempt",
            configuration = new Dictionary<string, string>
            {
                ["webhookUrl"] = "https://hooks.slack.com/services/T123/B456/abc"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Unauthenticated_Returns401()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/integrations");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    // --- Provider Validation Tests ---

    [Test]
    public async Task Create_InvalidDiscordUrl_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "Discord",
            name = "Bad Discord",
            configuration = new Dictionary<string, string>
            {
                ["webhookUrl"] = "https://not-discord.com/hooks/123"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("discord.com/api/webhooks/");
    }

    [Test]
    public async Task Create_InvalidTeamsUrl_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "MicrosoftTeams",
            name = "Bad Teams",
            configuration = new Dictionary<string, string>
            {
                ["webhookUrl"] = "https://not-teams.com/webhook/abc"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("webhook.office.com/");
    }

    [Test]
    public async Task Create_InvalidPagerDutyRoutingKey_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "PagerDuty",
            name = "Bad PagerDuty",
            configuration = new Dictionary<string, string>
            {
                // Too short (only 10 chars) and should be 32 hex chars
                ["routingKey"] = "abc123def0"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("32-character hexadecimal");
    }

    [Test]
    public async Task Create_CustomWithHttpUrl_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "Custom",
            name = "Insecure Custom",
            configuration = new Dictionary<string, string>
            {
                ["url"] = "http://insecure.example.com/webhook"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("https://");
    }

    // --- Update Validation Tests ---

    [Test]
    public async Task Update_InvalidSlackConfiguration_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db, SubscriptionTier.Pro);
        int integrationId = await SeedIntegration(db, tenantId, userId, IntegrationProvider.Slack, "Slack To Validate");

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/integrations/{integrationId}", new
        {
            configuration = new Dictionary<string, string>
            {
                ["webhookUrl"] = "https://not-slack.com/bad"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("hooks.slack.com/services/");
    }

    [Test]
    public async Task Update_ValidConfiguration_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db, SubscriptionTier.Pro);
        int integrationId = await SeedIntegration(db, tenantId, userId, IntegrationProvider.Slack, "Slack To Update");

        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/integrations/{integrationId}", new
        {
            configuration = new Dictionary<string, string>
            {
                ["webhookUrl"] = "https://hooks.slack.com/services/T123/B456/newurl"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // --- Cross-Tenant Delete Test ---

    [Test]
    public async Task Delete_OtherTenantIntegration_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantIdA, int userIdA) = await SeedTenantAndUser(db);
        (int tenantIdB, int userIdB) = await SeedTenantAndUser(db);

        // Create integration in tenant A
        int integrationId = await SeedIntegration(db, tenantIdA, userIdA, IntegrationProvider.Slack, "Tenant A Integration");

        // Try to delete it as tenant B
        HttpClient clientB = BuildClient(factory, tenantIdB, userIdB);
        HttpResponseMessage response = await clientB.DeleteAsync($"/api/v1/integrations/{integrationId}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // --- Transactional Audit Log Tests ---

    [Test]
    public async Task Create_HappyPath_WritesAuditLogEntry()
    {
        // Intent: the integration create and its audit log entry must be written in the same
        // transaction. This test confirms the transactional path still records the audit row on
        // a successful create.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "Slack",
            name = "Audit Log Test Integration",
            configuration = new Dictionary<string, string>
            {
                ["webhookUrl"] = "https://hooks.slack.com/services/T123/B456/audit"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        AuditLogEntry? auditEntry = await db.AuditLog
            .Where(a => (a.TenantId == tenantId) && (a.Action == AuditAction.IntegrationCreated))
            .FirstOrDefaultAsync();

        await Assert.That(auditEntry).IsNotNull();
        await Assert.That(auditEntry!.ResourceType).IsEqualTo(AuditResourceType.Integration);
    }

    [Test]
    public async Task DbHardDeleteIntegration_CascadesDeliveryAttempts()
    {
        // Intent: prove that SQLite foreign-key enforcement (newly turned ON in
        // FunctionalTestFactory) cascades a hard delete from IntegrationEndpoints to
        // IntegrationDeliveryAttempts as declared by InitialMigration's
        // `OnDelete(Rule.Cascade)` clause. Mirrors the unit-level
        // IntegrationDeliveryAttemptRepositoryTests.IntegrationEndpointDelete_CascadesToIntegrationDeliveryAttempts
        // test but runs against the functional factory's connection — which is the
        // surface used by every HTTP endpoint test — so a future regression that
        // strips the cascade (or disables FKs again) is caught at the same layer
        // where ~570 functional tests now rely on FK enforcement.
        //
        // Note: the public IntegrationDelete endpoint performs a soft-delete, so it
        // does not exercise SQL cascade. This test goes directly through DatabaseContext
        // to verify the underlying DB-level contract.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        int integrationId = await SeedIntegration(db, tenantId, userId);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "Cascade Test Rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        AlertEvent evt = new()
        {
            AlertRuleId = rule.Id,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Warning,
            Message = "Cascade test event",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        long eventId = await db.InsertWithInt64IdentityAsync(evt);

        IntegrationDeliveryAttempt attempt = new()
        {
            AlertEventId = eventId,
            IntegrationEndpointId = integrationId,
            Status = IntegrationDeliveryAttemptStatus.Succeeded,
            AttemptedAt = DateTimeOffset.UtcNow,
            SucceededAt = DateTimeOffset.UtcNow,
        };
        await db.InsertAsync(attempt);

        int attemptsBefore = await db.IntegrationDeliveryAttempts
            .Where(a => a.IntegrationEndpointId == integrationId)
            .CountAsync();
        await Assert.That(attemptsBefore).IsEqualTo(1);

        int deleted = await db.IntegrationEndpoints
            .Where(i => i.Id == integrationId)
            .DeleteAsync();
        await Assert.That(deleted).IsEqualTo(1);

        int attemptsAfter = await db.IntegrationDeliveryAttempts
            .Where(a => a.IntegrationEndpointId == integrationId)
            .CountAsync();
        await Assert.That(attemptsAfter).IsEqualTo(0);
    }
}
