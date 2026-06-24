// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests asserting the email-domain SSO lookup (<c>POST /api/v1/auth/email-lookup</c>)
/// is non-enumerable: it returns an identically shaped, opaque response on hit and miss and never
/// exposes the raw numeric tenant id.
/// </summary>
public sealed class EmailDomainLookupSecurityTests
{
    private static async Task<int> SeedTenantWithOidc(DatabaseContext db, string emailDomain)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"SSO Lookup Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        await db.InsertAsync(new TenantOidcConfiguration
        {
            TenantId = tenant.Id,
            Authority = "https://1.1.1.1/oidc",
            ClientId = "client-id-xyz",
            ClientSecret = "vord-protected:ciphertext-not-shown-to-client",
            EmailDomain = emailDomain,
            MetadataAddress = "https://1.1.1.1/oidc/.well-known/openid-configuration",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        return tenant.Id;
    }

    private static async Task<JsonElement> PostLookup(HttpClient client, string email)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/email-lookup", new { email });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);

        return doc.RootElement.Clone();
    }

    [Test]
    public async Task EmailLookup_HitAndMiss_HaveIdenticalShape_AndNoRawTenantId()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithOidc(db, "ssolookup-corp.test");

        HttpClient client = factory.CreateClient();

        JsonElement hit = await PostLookup(client, "user@ssolookup-corp.test");
        JsonElement miss = await PostLookup(client, "user@no-such-domain-here.test");

        JsonElement hitData = hit.GetProperty("data");
        JsonElement missData = miss.GetProperty("data");

        // Identical key set on hit and miss.
        List<string> hitKeys = [.. hitData.EnumerateObject().Select(p => p.Name).OrderBy(n => n)];
        List<string> missKeys = [.. missData.EnumerateObject().Select(p => p.Name).OrderBy(n => n)];
        await Assert.That(hitKeys).IsEquivalentTo(missKeys);

        // Both expose an ssoAvailable boolean.
        await Assert.That(hitData.GetProperty("ssoAvailable").GetBoolean()).IsTrue();
        await Assert.That(missData.GetProperty("ssoAvailable").GetBoolean()).IsFalse();

        // Neither response carries a raw tenant id key.
        await Assert.That(hitData.TryGetProperty("tenantId", out _)).IsFalse();
        await Assert.That(missData.TryGetProperty("tenantId", out _)).IsFalse();

        // The opaque slug must not be the decimal tenant id, and the body must not surface it as a
        // discrete JSON value.
        string slug = hitData.GetProperty("slug").GetString()!;
        await Assert.That(slug).IsNotEqualTo(tenantId.ToString());
        await Assert.That(hit.GetRawText()).DoesNotContain($":{tenantId}");
        await Assert.That(hit.GetRawText()).DoesNotContain($"\"{tenantId}\"");
    }

    [Test]
    public async Task EmailLookup_InvalidEmail_ReturnsSameUnavailableShape()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        JsonElement result = await PostLookup(client, "not-an-email");
        JsonElement data = result.GetProperty("data");

        await Assert.That(data.GetProperty("ssoAvailable").GetBoolean()).IsFalse();
        await Assert.That(data.TryGetProperty("tenantId", out _)).IsFalse();
    }

    [Test]
    public async Task EmailLookup_Slug_RoundTripsToTenant_ViaChallenge()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithOidc(db, "roundtrip-corp.test");

        // The seeded tenant has no Team subscription, so the challenge must reject with 400
        // ("Custom SSO is not available") rather than 500 — proving the slug resolved to a real
        // tenant and flowed into the existing tier/enabled checks.
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        JsonElement hit = await PostLookup(client, "user@roundtrip-corp.test");
        string slug = hit.GetProperty("data").GetProperty("slug").GetString()!;

        await Assert.That(slug).IsNotNull();
        await Assert.That(slug).IsNotEqualTo(tenantId.ToString());

        HttpResponseMessage challenge = await client.GetAsync(
            $"/api/v1/auth/challenge/tenant-oidc?slug={Uri.EscapeDataString(slug)}");

        await Assert.That(challenge.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string challengeBody = await challenge.Content.ReadAsStringAsync();
        await Assert.That(challengeBody).Contains("Custom SSO is not available");
    }

    [Test]
    public async Task Challenge_UnresolvableSlug_Returns400()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage challenge = await client.GetAsync(
            "/api/v1/auth/challenge/tenant-oidc?slug=not-a-valid-slug");

        await Assert.That(challenge.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
