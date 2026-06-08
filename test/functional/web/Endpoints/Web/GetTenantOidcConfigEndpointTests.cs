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

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the custom-OIDC GET endpoint (<c>GET /api/v1/tenants/{id}/oidc</c>).
/// Verifies the tier gate (Team-only), the tenant-claim isolation, and that the returned DTO
/// masks the encrypted client secret with the conventional "********" placeholder.
/// </summary>
public sealed class GetTenantOidcConfigEndpointTests
{
    private sealed record SeededTenant(int TenantId, int UserId, SubscriptionTier Tier);

    private static async Task<SeededTenant> SeedTenantWithSubscription(DatabaseContext db, SubscriptionTier tier)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"OIDC Get Tenant {Guid.NewGuid():N}",
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
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-oidc-get-{Guid.NewGuid():N}",
            Username = $"oidc-get-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        await db.InsertAsync(new UserTenantRole
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        return new SeededTenant(tenant.Id, user.Id, tier);
    }

    private static HttpClient BuildTenantAdminClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    [Test]
    public async Task GetOidc_TeamTier_ExistingConfig_ReturnsMaskedDto()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        await db.InsertAsync(new TenantOidcConfiguration
        {
            TenantId = seeded.TenantId,
            Authority = "https://1.1.1.1/oidc",
            ClientId = "client-id-xyz",
            ClientSecret = "vord-protected:ciphertext-not-shown-to-client",
            EmailDomain = "tenantcorp.test",
            MetadataAddress = "https://1.1.1.1/oidc/.well-known/openid-configuration",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        HttpClient client = BuildTenantAdminClient(factory, seeded.TenantId, seeded.UserId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/tenants/{seeded.TenantId}/oidc");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("authority").GetString()).IsEqualTo("https://1.1.1.1/oidc");
        await Assert.That(data.GetProperty("clientId").GetString()).IsEqualTo("client-id-xyz");
        await Assert.That(data.GetProperty("clientSecret").GetString()).IsEqualTo("********");
        await Assert.That(data.GetProperty("emailDomain").GetString()).IsEqualTo("tenantcorp.test");
        await Assert.That(data.GetProperty("isEnabled").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task GetOidc_TeamTier_NoConfigYet_ReturnsEmptyDto()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = BuildTenantAdminClient(factory, seeded.TenantId, seeded.UserId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/tenants/{seeded.TenantId}/oidc");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("authority").GetString()).IsEqualTo(string.Empty);
        await Assert.That(data.GetProperty("clientSecret").GetString()).IsEqualTo(string.Empty);
        await Assert.That(data.GetProperty("isEnabled").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task GetOidc_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Free);

        HttpClient client = BuildTenantAdminClient(factory, seeded.TenantId, seeded.UserId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/tenants/{seeded.TenantId}/oidc");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Team");
    }

    [Test]
    public async Task GetOidc_ProTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Pro);

        HttpClient client = BuildTenantAdminClient(factory, seeded.TenantId, seeded.UserId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/tenants/{seeded.TenantId}/oidc");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task GetOidc_CrossTenantAttempt_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant tenantA = await SeedTenantWithSubscription(db, SubscriptionTier.Team);
        SeededTenant tenantB = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        // Caller's active tenant is A but they ask for B's config — handler returns NotFound
        // because the claim-tenant id does not match the route id.
        HttpClient client = BuildTenantAdminClient(factory, tenantA.TenantId, tenantA.UserId);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/tenants/{tenantB.TenantId}/oidc");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("not found");
    }

    [Test]
    public async Task GetOidc_AsViewer_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(seeded.UserId)
            .WithRole(seeded.TenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(seeded.TenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/tenants/{seeded.TenantId}/oidc");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task GetOidc_Unauthenticated_Returns401()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/tenants/1/oidc");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}
