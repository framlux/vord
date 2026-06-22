// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the custom-OIDC PUT endpoint (<c>PUT /api/v1/tenants/{id}/oidc</c>).
/// Covers the validator (HTTPS required + required fields), the handler's tier gate,
/// tenant-claim isolation, the SSRF URL-safety check, and the "mask placeholder ⇒ keep existing
/// secret" upsert path.
///
/// Authority URLs in these tests use the literal public IP <c>1.1.1.1</c> so that
/// <see cref="Framlux.FleetManagement.Server.Auth.SsoOidcEvents.IsUrlSafeAsync"/> short-circuits
/// without performing a DNS lookup — making the tests deterministic regardless of the network
/// available to the test runner.
/// </summary>
public sealed class UpdateTenantOidcConfigEndpointTests
{
    private const string ValidAuthority = "https://1.1.1.1/oidc";
    private const string ValidMetadata = "https://1.1.1.1/oidc/.well-known/openid-configuration";

    private sealed record SeededTenant(int TenantId, int UserId);

    private static async Task<SeededTenant> SeedTenantWithSubscription(DatabaseContext db, SubscriptionTier tier)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"OIDC Put Tenant {Guid.NewGuid():N}",
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
            ExternalId = $"ext-oidc-put-{Guid.NewGuid():N}",
            Username = $"oidc-put-{Guid.NewGuid():N}@example.com",
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

        return new SeededTenant(tenant.Id, user.Id);
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
    public async Task PutOidc_TeamTier_FirstWrite_PersistsEncryptedSecretAndReturnsMaskedDto()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = BuildTenantAdminClient(factory, seeded.TenantId, seeded.UserId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/tenants/{seeded.TenantId}/oidc", new
        {
            Authority = ValidAuthority,
            ClientId = "first-client",
            ClientSecret = "plaintext-secret-value",
            MetadataAddress = ValidMetadata,
            EmailDomain = "ExampleCorp.Test",
            IsEnabled = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("authority").GetString()).IsEqualTo(ValidAuthority);
        await Assert.That(data.GetProperty("clientSecret").GetString()).IsEqualTo("********");

        // The email domain is normalized to lowercase + trimmed.
        await Assert.That(data.GetProperty("emailDomain").GetString()).IsEqualTo("examplecorp.test");

        // Verify the database row stores an ENCRYPTED secret, not the plaintext we posted.
        TenantOidcConfiguration? row = await db.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == seeded.TenantId);
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.ClientSecret).IsNotEqualTo("plaintext-secret-value");
        await Assert.That(row.ClientSecret).StartsWith(IOidcSecretProtector.ProtectedMarker);
        await Assert.That(row.EmailDomain).IsEqualTo("examplecorp.test");
    }

    [Test]
    public async Task PutOidc_TeamTier_UpdateWithMaskedSecret_PreservesExistingSecret()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = BuildTenantAdminClient(factory, seeded.TenantId, seeded.UserId);

        // First create with a real secret.
        await client.PutAsJsonAsync($"/api/v1/tenants/{seeded.TenantId}/oidc", new
        {
            Authority = ValidAuthority,
            ClientId = "client-1",
            ClientSecret = "original-secret",
            EmailDomain = "first.test",
            IsEnabled = true,
        });

        TenantOidcConfiguration originalRow = (await db.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == seeded.TenantId))!;
        string originalCiphertext = originalRow.ClientSecret;

        // Second PUT supplies the masked placeholder — handler should retain the original ciphertext.
        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/tenants/{seeded.TenantId}/oidc", new
        {
            Authority = ValidAuthority,
            ClientId = "client-1-updated",
            ClientSecret = "********",
            EmailDomain = "second.test",
            IsEnabled = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        TenantOidcConfiguration updatedRow = (await db.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == seeded.TenantId))!;
        await Assert.That(updatedRow.ClientId).IsEqualTo("client-1-updated");
        await Assert.That(updatedRow.EmailDomain).IsEqualTo("second.test");
        await Assert.That(updatedRow.IsEnabled).IsFalse();
        await Assert.That(updatedRow.ClientSecret).IsEqualTo(originalCiphertext);
    }

    [Test]
    public async Task PutOidc_TeamTier_NonHttpsAuthority_Returns400FromValidator()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = BuildTenantAdminClient(factory, seeded.TenantId, seeded.UserId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/tenants/{seeded.TenantId}/oidc", new
        {
            Authority = "http://1.1.1.1/oidc",
            ClientId = "client-1",
            ClientSecret = "secret",
            EmailDomain = "test.example",
            IsEnabled = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("HTTPS");
    }

    [Test]
    public async Task PutOidc_TeamTier_MissingRequiredFields_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = BuildTenantAdminClient(factory, seeded.TenantId, seeded.UserId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/tenants/{seeded.TenantId}/oidc", new
        {
            Authority = "",
            ClientId = "",
            ClientSecret = "",
            EmailDomain = "",
            IsEnabled = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("errors").GetArrayLength()).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task PutOidc_TeamTier_PrivateAuthorityIp_Returns400FromSsrfCheck()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = BuildTenantAdminClient(factory, seeded.TenantId, seeded.UserId);

        // The validator accepts the URL (it's HTTPS) but the handler's IsUrlSafeAsync check
        // rejects RFC1918 private IPs to block SSRF.
        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/tenants/{seeded.TenantId}/oidc", new
        {
            Authority = "https://10.0.0.5/oidc",
            ClientId = "client-1",
            ClientSecret = "secret",
            EmailDomain = "test.example",
            IsEnabled = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Authority");
    }

    [Test]
    public async Task PutOidc_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Free);

        HttpClient client = BuildTenantAdminClient(factory, seeded.TenantId, seeded.UserId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/tenants/{seeded.TenantId}/oidc", new
        {
            Authority = ValidAuthority,
            ClientId = "client-1",
            ClientSecret = "secret",
            EmailDomain = "test.example",
            IsEnabled = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // Free-tier tenants must not get an OIDC row created behind the gate.
        TenantOidcConfiguration? row = await db.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == seeded.TenantId);
        await Assert.That(row).IsNull();
    }

    [Test]
    public async Task PutOidc_CrossTenantAttempt_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant tenantA = await SeedTenantWithSubscription(db, SubscriptionTier.Team);
        SeededTenant tenantB = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = BuildTenantAdminClient(factory, tenantA.TenantId, tenantA.UserId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/tenants/{tenantB.TenantId}/oidc", new
        {
            Authority = ValidAuthority,
            ClientId = "client-1",
            ClientSecret = "secret",
            EmailDomain = "test.example",
            IsEnabled = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // Tenant B's row should not have been written.
        TenantOidcConfiguration? row = await db.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == tenantB.TenantId);
        await Assert.That(row).IsNull();
    }

    [Test]
    public async Task PutOidc_AsViewer_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(seeded.UserId)
            .WithRole(seeded.TenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(seeded.TenantId)
            .Build();

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/tenants/{seeded.TenantId}/oidc", new
        {
            Authority = ValidAuthority,
            ClientId = "client-1",
            ClientSecret = "secret",
            EmailDomain = "test.example",
            IsEnabled = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task PutOidc_Unauthenticated_Returns401()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/tenants/1/oidc", new
        {
            Authority = ValidAuthority,
            ClientId = "c",
            ClientSecret = "s",
            EmailDomain = "x.test",
            IsEnabled = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task PutOidc_TeamTier_WritesExactlyOneAuditLogEntry()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        SeededTenant seeded = await SeedTenantWithSubscription(db, SubscriptionTier.Team);

        HttpClient client = BuildTenantAdminClient(factory, seeded.TenantId, seeded.UserId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/tenants/{seeded.TenantId}/oidc", new
        {
            Authority = ValidAuthority,
            ClientId = "audit-client",
            ClientSecret = "audit-secret",
            EmailDomain = "audit.test",
            IsEnabled = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        List<AuditLogEntry> entries = await db.AuditLog
            .Where(e => e.Action == AuditAction.TenantOidcConfigured
                        && e.ResourceType == AuditResourceType.TenantOidcConfig
                        && e.TenantId == seeded.TenantId)
            .ToListAsync();

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].UserId).IsEqualTo(seeded.UserId);
    }
}
