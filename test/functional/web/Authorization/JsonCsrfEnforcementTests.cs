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
/// <para>
/// A page keeps the request token it captured at load, so the antiforgery cookie must stay stable
/// for a browser: the tests also pin the framework behaviour the web relies on to achieve that. A
/// bootstrap that forwards the cookie it holds is answered with a token for that same cookie and no
/// replacement, an unreadable cookie is replaced, and a token never validates for another user.
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
        UserAccount user = await InsertUserAsync(factory);
        string externalId = user.ExternalId;

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

    [Test]
    public async Task AuthMe_WithValidAntiforgeryCookie_ReusesItAndIssuesNoNewCookie()
    {
        using FunctionalTestFactory factory = new();
        UserAccount user = await InsertUserAsync(factory);
        HttpClient client = BuildClient(factory, user);

        HttpResponseMessage first = await GetAuthMeAsync(client, antiforgeryCookie: null);
        string cookie = ExtractAntiforgeryCookieValue(first);

        // The browser now holds the cookie and presents it on the next bootstrap. The framework must
        // recognise it as its own and answer with a token for it, without minting a replacement.
        HttpResponseMessage second = await GetAuthMeAsync(client, cookie);

        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(FindAntiforgeryCookieValue(second)).IsNull();
    }

    [Test]
    public async Task AuthMe_WithValidAntiforgeryCookie_ReturnsTokenThatPassesTheJsonGate()
    {
        using FunctionalTestFactory factory = new();
        UserAccount user = await InsertUserAsync(factory);
        HttpClient client = BuildClient(factory, user);

        HttpResponseMessage first = await GetAuthMeAsync(client, antiforgeryCookie: null);
        string cookie = ExtractAntiforgeryCookieValue(first);

        HttpResponseMessage second = await GetAuthMeAsync(client, cookie);
        string reusedToken = await ExtractCsrfTokenFromBody(second);

        HttpResponseMessage post = await PostContactAsync(client, cookie, reusedToken);

        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task JsonPost_WithTokenMintedEarlier_StillPasses_AfterLaterAuthMeForwardedTheSameCookie()
    {
        using FunctionalTestFactory factory = new();
        UserAccount user = await InsertUserAsync(factory);
        HttpClient client = BuildClient(factory, user);

        // The page captured this token at load time.
        HttpResponseMessage first = await GetAuthMeAsync(client, antiforgeryCookie: null);
        string cookie = ExtractAntiforgeryCookieValue(first);
        string tokenCapturedByThePage = await ExtractCsrfTokenFromBody(first);

        // A background poll bootstraps again with the cookie the browser holds, as the web does.
        await GetAuthMeAsync(client, cookie);
        await GetAuthMeAsync(client, cookie);

        HttpResponseMessage post = await PostContactAsync(client, cookie, tokenCapturedByThePage);

        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task JsonPost_WithTokenMintedEarlier_Returns400_WhenLaterAuthMeDidNotForwardTheCookie()
    {
        using FunctionalTestFactory factory = new();
        UserAccount user = await InsertUserAsync(factory);
        HttpClient client = BuildClient(factory, user);

        HttpResponseMessage first = await GetAuthMeAsync(client, antiforgeryCookie: null);
        string tokenCapturedByThePage = await ExtractCsrfTokenFromBody(first);

        // A bootstrap that leaves the browser's cookie out gets a different cookie minted for it,
        // and the browser ends up holding that one. This is the defect the web must never cause:
        // the token the page captured no longer pairs with the cookie the browser sends.
        HttpResponseMessage second = await GetAuthMeAsync(client, antiforgeryCookie: null);
        string replacementCookie = ExtractAntiforgeryCookieValue(second);

        HttpResponseMessage post = await PostContactAsync(client, replacementCookie, tokenCapturedByThePage);

        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await post.Content.ReadAsStringAsync()).Contains("Antiforgery validation failed.");
    }

    [Test]
    public async Task AuthMe_IssuedAntiforgeryCookie_UsesOnlyTheBase64UrlAlphabet()
    {
        using FunctionalTestFactory factory = new();
        UserAccount user = await InsertUserAsync(factory);
        HttpClient client = BuildClient(factory, user);

        // The web only forwards a vord_csrf value made of these characters and treats anything else as
        // a cookie the backend never issued, so the cookie the framework writes must never leave the
        // alphabet: no percent-escapes, no base64 padding, no '+' or '/'.
        for (int i = 0; i < 25; i++)
        {
            HttpResponseMessage response = await GetAuthMeAsync(client, antiforgeryCookie: null);
            string cookie = ExtractAntiforgeryCookieValue(response);

            await Assert.That(cookie).IsNotEmpty();
            await Assert.That(cookie.All(c => char.IsAsciiLetterOrDigit(c) || (c == '-') || (c == '_'))).IsTrue();
        }
    }

    [Test]
    public async Task AuthMe_WithBogusAntiforgeryCookie_IssuesAFreshPairThatPassesTheJsonGate()
    {
        using FunctionalTestFactory factory = new();
        UserAccount user = await InsertUserAsync(factory);
        HttpClient client = BuildClient(factory, user);
        const string bogusCookie = "not-a-real-antiforgery-cookie";

        HttpResponseMessage response = await GetAuthMeAsync(client, bogusCookie);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // A cookie the framework cannot read is replaced, so a stale or tampered cookie heals itself
        // on the next page load.
        string? replacement = FindAntiforgeryCookieValue(response);
        await Assert.That(replacement).IsNotNull();
        await Assert.That(replacement).IsNotEqualTo(bogusCookie);

        string token = await ExtractCsrfTokenFromBody(response);

        HttpResponseMessage withNewCookie = await PostContactAsync(client, replacement!, token);
        HttpResponseMessage withBogusCookie = await PostContactAsync(client, bogusCookie, token);

        await Assert.That(withNewCookie.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(withBogusCookie.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task JsonPost_WithTokenOfAnotherUser_Returns400_EvenWhenTheCookieIsShared()
    {
        using FunctionalTestFactory factory = new();
        UserAccount userA = await InsertUserAsync(factory);
        UserAccount userB = await InsertUserAsync(factory);
        HttpClient clientA = BuildClient(factory, userA);
        HttpClient clientB = BuildClient(factory, userB);

        HttpResponseMessage meA = await GetAuthMeAsync(clientA, antiforgeryCookie: null);
        string sharedCookie = ExtractAntiforgeryCookieValue(meA);
        string tokenOfA = await ExtractCsrfTokenFromBody(meA);

        // The same browser signs in as someone else and keeps its cookie: it is reused, and the
        // token issued with it names the new user.
        HttpResponseMessage meB = await GetAuthMeAsync(clientB, sharedCookie);
        string tokenOfB = await ExtractCsrfTokenFromBody(meB);

        HttpResponseMessage withOwnToken = await PostContactAsync(clientB, sharedCookie, tokenOfB);
        HttpResponseMessage withForeignToken = await PostContactAsync(clientB, sharedCookie, tokenOfA);

        await Assert.That(FindAntiforgeryCookieValue(meB)).IsNull();
        await Assert.That(withOwnToken.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(withForeignToken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task JsonPost_WithTokenMintedEarlier_StillPasses_AfterTenantSwitchAndRoleChange()
    {
        using FunctionalTestFactory factory = new();
        UserAccount user = await InsertUserAsync(factory);
        int firstTenantId = await InsertTenantAsync(factory);
        int secondTenantId = await InsertTenantAsync(factory);

        HttpClient beforeSwitch = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId(user.ExternalId)
            .WithActiveTenant(firstTenantId)
            .WithRole(firstTenantId, (int)UserAccountRoles.Viewer)
            .Build();

        HttpResponseMessage first = await GetAuthMeAsync(beforeSwitch, antiforgeryCookie: null);
        string cookie = ExtractAntiforgeryCookieValue(first);
        string tokenCapturedByThePage = await ExtractCsrfTokenFromBody(first);

        // The same session after the user switched tenant and was promoted: the principal's claims
        // changed, but the identity the token names (the subject) did not.
        HttpClient afterSwitch = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId(user.ExternalId)
            .WithActiveTenant(secondTenantId)
            .WithRole(firstTenantId, (int)UserAccountRoles.TenantAdmin)
            .WithRole(secondTenantId, (int)UserAccountRoles.TenantAdmin)
            .Build();

        HttpResponseMessage post = await PostContactAsync(afterSwitch, cookie, tokenCapturedByThePage);

        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static HttpClient BuildClient(FunctionalTestFactory factory, UserAccount user)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId(user.ExternalId)
            .Build();
    }

    /// <summary>
    /// Inserts a distinct active user so each caller gets its own antiforgery identity.
    /// </summary>
    private static async Task<UserAccount> InsertUserAsync(FunctionalTestFactory factory)
    {
        using DatabaseContext db = factory.CreateDbContext();

        UserAccount user = new()
        {
            ExternalId = $"ext-csrf-{Guid.NewGuid():N}",
            Username = $"csrf-{Guid.NewGuid():N}@example.com",
            AuthProvider = AuthProviderType.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        return user;
    }

    /// <summary>
    /// Inserts an active tenant with an active subscription so a request carrying it as the active
    /// tenant is not refused.
    /// </summary>
    private static async Task<int> InsertTenantAsync(FunctionalTestFactory factory)
    {
        using DatabaseContext db = factory.CreateDbContext();

        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"csrf-tenant-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = string.Empty,
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

        return tenant.Id;
    }

    /// <summary>
    /// Calls <c>/api/v1/auth/me</c> the way the web bootstrap does: with the auth cookie and, when
    /// the browser holds one, the antiforgery cookie.
    /// </summary>
    private static async Task<HttpResponseMessage> GetAuthMeAsync(HttpClient client, string? antiforgeryCookie)
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Add(
            "Cookie",
            antiforgeryCookie is null
                ? "vord_auth=session-token"
                : $"vord_auth=session-token; vord_csrf={antiforgeryCookie}");

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Sends a cookie-authenticated JSON POST that carries the given double-submit pair.
    /// </summary>
    private static async Task<HttpResponseMessage> PostContactAsync(HttpClient client, string antiforgeryCookie, string requestToken)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/contact");
        request.Headers.Add("Cookie", $"vord_auth=session-token; vord_csrf={antiforgeryCookie}");
        request.Headers.Add("X-CSRF-TOKEN", requestToken);
        request.Content = JsonContent.Create(new
        {
            Name = "Legit User",
            Email = "legit@example.com",
            Company = "",
            FleetSize = "",
            Message = "Same-origin JSON POST carrying a double-submit pair.",
        });

        return await client.SendAsync(request);
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
    /// <c>IAntiforgery.GetAndStoreTokens</c>, failing when the response issued none.
    /// </summary>
    private static string ExtractAntiforgeryCookieValue(HttpResponseMessage response)
    {
        return FindAntiforgeryCookieValue(response)
            ?? throw new InvalidOperationException("Antiforgery cookie was not set by auth/me.");
    }

    /// <summary>
    /// Returns the antiforgery cookie value the response set, or null when it set none. Reuse of an
    /// existing cookie is asserted through this absence, since the extractor above throws instead.
    /// </summary>
    private static string? FindAntiforgeryCookieValue(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies) == false)
        {
            return null;
        }

        foreach (string setCookie in setCookies)
        {
            if (setCookie.StartsWith("vord_csrf=", StringComparison.Ordinal))
            {
                string remainder = setCookie["vord_csrf=".Length..];
                int semicolon = remainder.IndexOf(';');

                return semicolon >= 0 ? remainder[..semicolon] : remainder;
            }
        }

        return null;
    }
}
