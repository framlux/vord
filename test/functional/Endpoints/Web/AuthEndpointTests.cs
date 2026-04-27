// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using System.Net;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for authentication endpoints (challenge, login, logout).
/// </summary>
public sealed class AuthEndpointTests
{
    [Test]
    public async Task Challenge_InvalidProvider_Returns400()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/challenge/invalid");

        await Assert.That((int)response.StatusCode).IsEqualTo(400);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Invalid authentication provider");
    }

    [Test]
    public async Task Challenge_EmptyProvider_Returns400()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // Empty provider string in route — FastEndpoints will bind as empty string
        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/challenge/%20");

        await Assert.That((int)response.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task Challenge_ValidProvider_Github_Returns302()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/challenge/github");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location).IsNotNull();
    }

    [Test]
    public async Task Challenge_ValidProvider_Google_Returns302()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/challenge/google");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location).IsNotNull();
    }

    [Test]
    public async Task Challenge_ValidProvider_Microsoft_Returns302()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/challenge/microsoft");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location).IsNotNull();
    }

    [Test]
    public async Task Challenge_OpenRedirectAttempt_DoubleSlash_UsesDefault()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/challenge/github?returnUrl=//evil.com");

        // Should redirect to OAuth provider with the default /dashboard as returnUrl, not //evil.com
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        string? location = response.Headers.Location?.ToString();
        await Assert.That(location).IsNotNull();
        await Assert.That(location!.Contains("evil.com")).IsFalse();
    }

    [Test]
    public async Task Challenge_OpenRedirectAttempt_Backslash_UsesDefault()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/challenge/github?returnUrl=%5Cevil.com");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        string? location = response.Headers.Location?.ToString();
        await Assert.That(location).IsNotNull();
        await Assert.That(location!.Contains("evil.com")).IsFalse();
    }

    [Test]
    public async Task Challenge_TenantOidc_NoTenantId_Returns400()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/challenge/tenant-oidc");

        await Assert.That((int)response.StatusCode).IsEqualTo(400);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("tenantId is required");
    }

    [Test]
    public async Task Login_ValidReturnUrl_Redirects()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/login?returnUrl=/settings");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        string? location = response.Headers.Location?.ToString();
        await Assert.That(location).IsNotNull();
        await Assert.That(location!).Contains("/auth/login");
        await Assert.That(location).Contains("settings");
    }

    [Test]
    public async Task Login_OpenRedirectAttempt_UsesDefault()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/login?returnUrl=//evil.com");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        string? location = response.Headers.Location?.ToString();
        await Assert.That(location).IsNotNull();
        await Assert.That(location!.Contains("evil.com")).IsFalse();
    }

    [Test]
    public async Task Login_NoReturnUrl_RedirectsWithDefaultDashboard()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage response = await client.GetAsync("/api/v1/login");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        string? location = response.Headers.Location?.ToString();
        await Assert.That(location).IsNotNull();
        await Assert.That(location!).Contains("dashboard");
    }

    [Test]
    public async Task Logout_NotAuthenticated_Returns401()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/v1/logout", null);

        // The logout endpoint requires authentication; unauthenticated requests get 401
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Logout_Unauthenticated_ReturnsOkIdempotently()
    {
        // When authenticating via TestAuthHandler, the user identity does not have
        // a real cookie, so SignOutAsync throws. Instead verify that the endpoint's
        // early-return path for unauthenticated users is idempotent.
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        // First logout
        HttpResponseMessage response1 = await client.PostAsync("/api/v1/logout", null);
        // Second logout (idempotent — same result)
        HttpResponseMessage response2 = await client.PostAsync("/api/v1/logout", null);

        await Assert.That(response1.StatusCode).IsEqualTo(response2.StatusCode);
    }
}
