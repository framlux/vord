// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for anonymous (unauthenticated) REST endpoints.
/// </summary>
public sealed class AnonymousEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    #region ContactForm — Happy Path

    [Test]
    public async Task ContactForm_ValidSubmission_ReturnsSuccessWithThankYouMessage()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/contact", new
        {
            Name = "John Doe",
            Email = "john@example.com",
            Company = "Acme Corp",
            FleetSize = "100+",
            Message = "Interested in Team tier"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(true);
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("Thank you for your interest! We'll be in touch soon.");
        await Assert.That(root.TryGetProperty("data", out JsonElement _)).IsEqualTo(true);
    }

    [Test]
    public async Task ContactForm_ValidSubmission_WithoutOptionalFields_ReturnsSuccess()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // Company and FleetSize are optional; only Name, Email, and Message are required
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/contact", new
        {
            Name = "Jane Smith",
            Email = "jane@example.com",
            Company = "",
            FleetSize = "",
            Message = "Just exploring"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(true);
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("Thank you for your interest! We'll be in touch soon.");
    }

    #endregion

    #region ContactForm — Error Cases

    [Test]
    public async Task ContactForm_EmptyName_ReturnsBadRequestWithRequiredFieldsError()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/contact", new
        {
            Name = "",
            Email = "john@example.com",
            Company = "Acme Corp",
            FleetSize = "10",
            Message = "Hello"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(false);
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("Name, email, and message are required");
    }

    [Test]
    public async Task ContactForm_EmptyEmail_ReturnsBadRequestWithRequiredFieldsError()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/contact", new
        {
            Name = "John Doe",
            Email = "",
            Company = "Acme Corp",
            FleetSize = "10",
            Message = "Hello"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(false);
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("Name, email, and message are required");
    }

    [Test]
    public async Task ContactForm_EmptyMessage_ReturnsBadRequestWithRequiredFieldsError()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/contact", new
        {
            Name = "John Doe",
            Email = "john@example.com",
            Company = "Acme Corp",
            FleetSize = "10",
            Message = ""
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(false);
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("Name, email, and message are required");
    }

    [Test]
    public async Task ContactForm_WhitespaceOnlyName_ReturnsBadRequestWithRequiredFieldsError()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/contact", new
        {
            Name = "   ",
            Email = "john@example.com",
            Company = "Acme Corp",
            FleetSize = "10",
            Message = "Hello"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(false);
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("Name, email, and message are required");
    }

    [Test]
    public async Task ContactForm_AllRequiredFieldsMissing_ReturnsBadRequestWithRequiredFieldsError()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/contact", new
        {
            Name = "",
            Email = "",
            Company = "",
            FleetSize = "",
            Message = ""
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(false);
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("Name, email, and message are required");
    }

    #endregion

    #region EmailDomainLookup — Happy Path

    [Test]
    public async Task EmailDomainLookup_KnownDomain_ReturnsSuccessWithTenantId()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Seed a tenant with OIDC configuration
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = "SSO Tenant",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantOidcConfiguration oidcConfig = new()
        {
            TenantId = tenant.Id,
            Authority = "https://sso.corporate.com",
            ClientId = "client-123",
            ClientSecret = "encrypted-secret",
            EmailDomain = "corporate.com",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(oidcConfig);

        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/email-lookup", new
        {
            Email = "user@corporate.com"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(true);
        await Assert.That(root.GetProperty("data").GetProperty("tenantId").GetInt32()).IsEqualTo(tenant.Id);
        // Error response should not return error-specific fields on success
        await Assert.That(root.GetProperty("message").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task EmailDomainLookup_KnownDomain_CaseInsensitive_ReturnsSuccessWithTenantId()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = "Case Test Tenant",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantOidcConfiguration oidcConfig = new()
        {
            TenantId = tenant.Id,
            Authority = "https://sso.example.com",
            ClientId = "client-456",
            ClientSecret = "encrypted-secret-2",
            EmailDomain = "example.org",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(oidcConfig);

        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // Send email with mixed-case domain; the endpoint should normalize to lowercase
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/email-lookup", new
        {
            Email = "User@EXAMPLE.ORG"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(true);
        await Assert.That(root.GetProperty("data").GetProperty("tenantId").GetInt32()).IsEqualTo(tenant.Id);
    }

    #endregion

    #region EmailDomainLookup — Error Cases

    [Test]
    public async Task EmailDomainLookup_InvalidEmailFormat_ReturnsErrorWithValidationMessage()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/email-lookup", new
        {
            Email = "not-an-email"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(false);
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("A valid email address is required");
        await Assert.That(root.GetProperty("data").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task EmailDomainLookup_EmptyEmail_ReturnsErrorWithValidationMessage()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/email-lookup", new
        {
            Email = ""
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(false);
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("A valid email address is required");
        await Assert.That(root.GetProperty("data").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task EmailDomainLookup_WhitespaceOnlyEmail_ReturnsErrorWithValidationMessage()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/email-lookup", new
        {
            Email = "   "
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(false);
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("A valid email address is required");
    }

    [Test]
    public async Task EmailDomainLookup_UnknownDomain_ReturnsErrorWithNoSsoProviderMessage()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/email-lookup", new
        {
            Email = "user@nonexistent-domain.com"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(false);
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("No SSO provider found for this email domain");
        await Assert.That(root.GetProperty("data").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task EmailDomainLookup_DisabledOidcConfig_ReturnsNoSsoProviderError()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Seed a tenant with a disabled OIDC configuration
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = "Disabled SSO Tenant",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantOidcConfiguration oidcConfig = new()
        {
            TenantId = tenant.Id,
            Authority = "https://sso.disabled.com",
            ClientId = "client-disabled",
            ClientSecret = "encrypted-secret",
            EmailDomain = "disabled-sso.com",
            IsEnabled = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(oidcConfig);

        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/email-lookup", new
        {
            Email = "user@disabled-sso.com"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // Disabled OIDC configs should not be returned as valid SSO providers
        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(false);
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("No SSO provider found for this email domain");
    }

    [Test]
    public async Task EmailDomainLookup_EmailWithLeadingTrailingSpaces_TrimsAndResolvesCorrectly()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = "Trim Test Tenant",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantOidcConfiguration oidcConfig = new()
        {
            TenantId = tenant.Id,
            Authority = "https://sso.trimtest.com",
            ClientId = "client-trim",
            ClientSecret = "encrypted-secret",
            EmailDomain = "trimtest.com",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(oidcConfig);

        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // Email with leading and trailing whitespace should be trimmed by the endpoint
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/email-lookup", new
        {
            Email = "  user@trimtest.com  "
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsEqualTo(true);
        await Assert.That(root.GetProperty("data").GetProperty("tenantId").GetInt32()).IsEqualTo(tenant.Id);
    }

    #endregion
}
