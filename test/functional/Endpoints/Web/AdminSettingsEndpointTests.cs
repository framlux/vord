// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for admin settings GET and PUT endpoints.
/// </summary>
public sealed class AdminSettingsEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedGlobalAdmin(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Admin Settings Tenant {Guid.NewGuid():N}",
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
            ExternalId = $"ext-admin-settings-{Guid.NewGuid():N}",
            Username = $"adminsettings-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = true,
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

    private static HttpClient BuildGlobalAdminClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .AsGlobalAdmin()
            .Build();
    }

    [Test]
    public async Task GetSettings_AsGlobalAdmin_ReturnsSettingsWithNameAndDescription()
    {
        using BillingDisabledTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedGlobalAdmin(db);

        await db.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "300",
            Version = 1,
        });

        HttpClient client = BuildGlobalAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/admin/settings");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        JsonElement settingsArray = root.GetProperty("data").GetProperty("settings");
        await Assert.That(settingsArray.GetArrayLength()).IsGreaterThanOrEqualTo(1);

        JsonElement firstSetting = settingsArray[0];
        await Assert.That(firstSetting.GetProperty("name").GetString()).IsNotEmpty();
        await Assert.That(firstSetting.GetProperty("description").GetString()).IsNotEmpty();
    }

    [Test]
    public async Task UpdateSettings_WhenBillingDisabled_UpdatesAndReturnsNewValues()
    {
        using BillingDisabledTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedGlobalAdmin(db);

        await db.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "300",
            Version = 1,
        });

        HttpClient client = BuildGlobalAdminClient(factory, tenantId, userId);

        string json = JsonSerializer.Serialize(new
        {
            settings = new[]
            {
                new { key = 1, value = "600" }
            }
        });
        StringContent content = new(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.PutAsync("/api/v1/admin/settings", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        JsonElement settingsArray = root.GetProperty("data").GetProperty("settings");
        JsonElement updatedSetting = settingsArray.EnumerateArray()
            .First(s => s.GetProperty("key").GetInt32() == 1);
        await Assert.That(updatedSetting.GetProperty("value").GetString()).IsEqualTo("600");
    }

    [Test]
    public async Task UpdateSettings_WhenBillingEnabled_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedGlobalAdmin(db);

        HttpClient client = BuildGlobalAdminClient(factory, tenantId, userId);

        string json = JsonSerializer.Serialize(new
        {
            settings = new[]
            {
                new { key = 1, value = "600" }
            }
        });
        StringContent content = new(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.PutAsync("/api/v1/admin/settings", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpdateSettings_AsNonAdmin_Returns403()
    {
        using BillingDisabledTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedGlobalAdmin(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        string json = JsonSerializer.Serialize(new
        {
            settings = new[]
            {
                new { key = 1, value = "600" }
            }
        });
        StringContent content = new(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.PutAsync("/api/v1/admin/settings", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task UpdateSettings_InvalidKey_Returns400()
    {
        using BillingDisabledTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedGlobalAdmin(db);
        HttpClient client = BuildGlobalAdminClient(factory, tenantId, userId);

        string json = JsonSerializer.Serialize(new
        {
            settings = new[]
            {
                new { key = 999, value = "100" }
            }
        });
        StringContent content = new(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await client.PutAsync("/api/v1/admin/settings", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task UpdateSettings_ReflectedInSubsequentGet()
    {
        using BillingDisabledTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedGlobalAdmin(db);

        await db.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.OnlineThresholdSeconds,
            Value = "300",
            Version = 1,
        });

        HttpClient client = BuildGlobalAdminClient(factory, tenantId, userId);

        string json = JsonSerializer.Serialize(new
        {
            settings = new[]
            {
                new { key = 3, value = "900" }
            }
        });
        StringContent content = new(json, Encoding.UTF8, "application/json");
        await client.PutAsync("/api/v1/admin/settings", content);

        HttpResponseMessage getResponse = await client.GetAsync("/api/v1/admin/settings");
        string body = await getResponse.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement settingsArray = doc.RootElement.GetProperty("data").GetProperty("settings");

        JsonElement updatedSetting = settingsArray.EnumerateArray()
            .First(s => s.GetProperty("key").GetInt32() == 3);
        await Assert.That(updatedSetting.GetProperty("value").GetString()).IsEqualTo("900");
    }
}
