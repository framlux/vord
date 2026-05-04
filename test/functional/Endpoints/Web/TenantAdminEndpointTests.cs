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
/// Functional tests for TenantAdmin-policy REST endpoints.
/// </summary>
public sealed class TenantAdminEndpointTests
{
    [Test]
    public async Task CreateRegistrationToken_ReturnsPlaintextToken()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/machines/registration-tokens", new
        {
            Name = "Test Token",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("\"token\":");

        // Verify hash in DB differs from plaintext
        RegistrationToken? dbToken = await db.RegistrationTokens.FirstOrDefaultAsync();
        await Assert.That(dbToken).IsNotNull();
        await Assert.That(body.Contains(dbToken!.TokenHash)).IsFalse();
    }

    [Test]
    public async Task RevokeRegistrationToken_MarksRevoked()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db);

        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = "hash-revoke-test",
            Name = "Revoke Me",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false,
        };
        token.Id = await db.InsertWithInt64IdentityAsync(token);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/machines/registration-tokens/{token.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        RegistrationToken? revoked = await db.RegistrationTokens.FirstOrDefaultAsync(t => t.Id == token.Id);
        await Assert.That(revoked!.IsRevoked).IsTrue();
        await Assert.That(revoked.RevokedAt.HasValue).IsTrue();
    }

    [Test]
    public async Task CreateInvitation_ValidEmail_CreatesInvitation()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db);

        UserAccount user = new()
        {
            ExternalId = "ext-invite-creator",
            Username = "admin@example.com",
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
            AssignedTenantId = tenantId,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId("ext-invite-creator")
            .WithEmail("admin@example.com")
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = "newuser@example.com"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");

        TenantInvitation? invitation = await db.TenantInvitations
            .FirstOrDefaultAsync(i => i.Email == "newuser@example.com");
        await Assert.That(invitation).IsNotNull();
        await Assert.That(invitation!.TenantId).IsEqualTo(tenantId);
    }

    [Test]
    public async Task RevokeInvitation_ChangesStatusToRevoked()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db);

        TenantInvitation invitation = new()
        {
            TenantId = tenantId,
            Email = "revoke@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        invitation.Id = await db.InsertWithInt32IdentityAsync(invitation);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync($"/api/v1/invitations/{invitation.Id}/revoke", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        TenantInvitation? revoked = await db.TenantInvitations.FirstOrDefaultAsync(i => i.Id == invitation.Id);
        await Assert.That(revoked!.Status).IsEqualTo(InvitationStatus.Revoked);
    }

    [Test]
    public async Task TenantOidcConfig_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Free);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/tenants/{tenantId}/oidc");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":false");
        await Assert.That(body).Contains("Team tier");
    }

    [Test]
    public async Task TenantOidcConfig_TeamTier_ReturnsConfig()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        TenantOidcConfiguration oidcConfig = new()
        {
            TenantId = tenantId,
            Authority = "https://sso.team.com",
            ClientId = "team-client",
            ClientSecret = "encrypted-secret",
            EmailDomain = "team.com",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertAsync(oidcConfig);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/tenants/{tenantId}/oidc");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("sso.team.com");
        // Secret should be masked
        await Assert.That(body).Contains("********");
    }

    // --- UpdateTenantOidcConfig Error Path Tests ---

    [Test]
    public async Task UpdateOidc_HttpAuthority_ReturnsErrorInBody()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/tenants/{tenantId}/oidc", new
        {
            Authority = "http://insecure-idp.example.com",
            ClientId = "client-id",
            ClientSecret = "client-secret",
            EmailDomain = "example.com",
            IsEnabled = true,
        });

        // The endpoint uses Send.OkAsync which sets HTTP 200, but body indicates failure
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":false");
        await Assert.That(body).Contains("HTTPS");
    }

    [Test]
    public async Task UpdateOidc_LocalhostAuthority_ReturnsErrorInBody()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/tenants/{tenantId}/oidc", new
        {
            Authority = "https://localhost/auth",
            ClientId = "client-id",
            ClientSecret = "client-secret",
            EmailDomain = "example.com",
            IsEnabled = true,
        });

        // Localhost is blocked by SSRF protection
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":false");
        await Assert.That(body).Contains("HTTPS");
    }

    [Test]
    public async Task UpdateOidc_ViewerRole_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

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

    private static async Task<int> SeedTenantWithSubscription(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Test Tenant {Guid.NewGuid():N}",
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
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        return tenant.Id;
    }
}
