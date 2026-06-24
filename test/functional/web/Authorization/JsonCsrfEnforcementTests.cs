// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;

namespace Framlux.FleetManagement.FunctionalTest.Authorization;

/// <summary>
/// End-to-end regression suite for the JSON antiforgery gate. The FastEndpoints antiforgery
/// middleware enforces only on form/multipart bodies, so a cookie-authenticated JSON mutation
/// would otherwise pass through unchecked. The inline gate added before the middleware closes
/// that hole: a non-safe request carrying the auth cookie must present a valid X-CSRF-TOKEN
/// header matching the antiforgery cookie, or the server returns 400.
/// <para>
/// The double-submit pair is obtained the same way the real web client obtains it: a GET to
/// <c>/api/v1/auth/me</c> issues the antiforgery cookie and returns the matching request token,
/// so the token is bound to the same authenticated identity that later submits the mutation.
/// The functional factory relaxes the antiforgery cookie SecurePolicy to SameAsRequest, so the
/// pair is mintable over the test host's HTTP transport.
/// </para>
/// </summary>
public sealed class JsonCsrfEnforcementTests
{
    [Test]
    public async Task JsonPost_WithAuthCookie_WithoutCsrfToken_Returns400()
    {
        using FunctionalTestFactory factory = new();

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .Build();

        // The auth cookie presence is what arms the gate, regardless of which scheme actually
        // authenticated the principal. No X-CSRF-TOKEN header is sent, so validation must fail.
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/contact");
        request.Headers.Add("Cookie", "vord_auth=session-token");
        request.Content = JsonContent.Create(new
        {
            Name = "Attacker",
            Email = "attacker@example.com",
            Company = "",
            FleetSize = "",
            Message = "Cross-site JSON POST without a token must be rejected.",
        });

        HttpResponseMessage response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Antiforgery validation failed.");
    }

    [Test]
    public async Task JsonPost_WithAuthCookie_WithValidCsrfToken_PassesCsrfGate()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-csrf-{Guid.NewGuid():N}";

        UserAccount user = new()
        {
            ExternalId = externalId,
            Username = $"csrf-{Guid.NewGuid():N}@example.com",
            AuthProvider = AuthProviderType.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        // AllowAutoRedirect=false, HandleCookies=false — the same client is reused for both the
        // token-issuing GET and the mutation POST, so we forward cookies manually.
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId(externalId)
            .Build();

        // Issue the double-submit pair through the real issuance path. The auth cookie arms the
        // gate even on the GET, but GET is a safe verb so it flows through and returns the token.
        HttpRequestMessage meRequest = new(HttpMethod.Get, "/api/v1/auth/me");
        meRequest.Headers.Add("Cookie", "vord_auth=session-token");
        HttpResponseMessage meResponse = await client.SendAsync(meRequest);

        await Assert.That(meResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string requestToken = await ExtractCsrfTokenFromBody(meResponse);
        string cookieToken = ExtractAntiforgeryCookieValue(meResponse);

        HttpRequestMessage postRequest = new(HttpMethod.Post, "/api/v1/contact");
        // Both halves of the double-submit pair: the auth cookie arms the gate, the antiforgery
        // cookie carries the secret, and the matching request token rides in the header.
        postRequest.Headers.Add("Cookie", $"vord_auth=session-token; vord_csrf={cookieToken}");
        postRequest.Headers.Add("X-CSRF-TOKEN", requestToken);
        postRequest.Content = JsonContent.Create(new
        {
            Name = "Legit User",
            Email = "legit@example.com",
            Company = "",
            FleetSize = "",
            Message = "Same-origin JSON POST with a valid token must pass the CSRF gate.",
        });

        HttpResponseMessage postResponse = await client.SendAsync(postRequest);

        // The gate must not reject this request. A 400 would mean the token was not accepted.
        await Assert.That(postResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await postResponse.Content.ReadAsStringAsync();
        await Assert.That(body).DoesNotContain("Antiforgery validation failed.");
    }

    /// <summary>
    /// Reads the <c>csrfToken</c> request token that the auth-me endpoint surfaces in its
    /// JSON body so the client can echo it in the X-CSRF-TOKEN header.
    /// </summary>
    private static async Task<string> ExtractCsrfTokenFromBody(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        string? token = document.RootElement
            .GetProperty("data")
            .GetProperty("csrfToken")
            .GetString();

        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("auth/me did not return a csrfToken.");
        }

        return token;
    }

    /// <summary>
    /// Reads the antiforgery cookie value that the auth-me endpoint wrote onto the response via
    /// <c>IAntiforgery.GetAndStoreTokens</c>.
    /// </summary>
    private static string ExtractAntiforgeryCookieValue(HttpResponseMessage response)
    {
        foreach (string setCookie in response.Headers.GetValues("Set-Cookie"))
        {
            if (setCookie.StartsWith("vord_csrf=", StringComparison.Ordinal))
            {
                string remainder = setCookie["vord_csrf=".Length..];
                int semicolon = remainder.IndexOf(';');

                return semicolon >= 0 ? remainder[..semicolon] : remainder;
            }
        }

        throw new InvalidOperationException("Antiforgery cookie was not set by auth/me.");
    }
}
