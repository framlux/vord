// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Startup;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Startup;

/// <summary>
/// Tests the antiforgery startup helpers: cookie/header option defaults, the runtime skip
/// predicate, the FastEndpoints configurator that opts state-changing endpoints into the
/// antiforgery middleware, and the canonical cookie-auth scheme name exposure.
/// </summary>
public sealed class AntiforgeryStartupTests
{
    // =================== ConfigureOptions ===================

    [Test]
    public async Task ConfigureOptions_SetsHeaderName()
    {
        AntiforgeryOptions options = new();

        AntiforgeryStartup.ConfigureOptions(options);

        await Assert.That(options.HeaderName).IsEqualTo("X-CSRF-TOKEN");
    }

    [Test]
    public async Task ConfigureOptions_SetsAntiforgeryCookieName()
    {
        AntiforgeryOptions options = new();

        AntiforgeryStartup.ConfigureOptions(options);

        await Assert.That(options.Cookie.Name).IsEqualTo("vord_csrf");
    }

    [Test]
    public async Task ConfigureOptions_SetsSameSiteStrict()
    {
        AntiforgeryOptions options = new();

        AntiforgeryStartup.ConfigureOptions(options);

        await Assert.That(options.Cookie.SameSite).IsEqualTo(SameSiteMode.Strict);
    }

    [Test]
    public async Task ConfigureOptions_SetsSecurePolicyAlways()
    {
        AntiforgeryOptions options = new();

        AntiforgeryStartup.ConfigureOptions(options);

        await Assert.That(options.Cookie.SecurePolicy).IsEqualTo(CookieSecurePolicy.Always);
    }

    [Test]
    public async Task ConfigureOptions_SetsHttpOnlyTrue()
    {
        AntiforgeryOptions options = new();

        AntiforgeryStartup.ConfigureOptions(options);

        await Assert.That(options.Cookie.HttpOnly).IsTrue();
    }

    [Test]
    public async Task ConfigureOptions_NullOptions_Throws()
    {
        await Assert.That(() => AntiforgeryStartup.ConfigureOptions(null!))
            .Throws<ArgumentNullException>();
    }

    // =================== ShouldSkipAntiforgery (cookie collection overload) ===================

    [Test]
    public async Task ShouldSkipAntiforgery_AuthCookiePresent_ReturnsFalse()
    {
        IRequestCookieCollection cookies = FakeCookies("vord_auth", "any-value");

        bool result = AntiforgeryStartup.ShouldSkipAntiforgery(cookies);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldSkipAntiforgery_AuthCookieAbsent_ReturnsTrue()
    {
        IRequestCookieCollection cookies = FakeCookies();

        bool result = AntiforgeryStartup.ShouldSkipAntiforgery(cookies);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldSkipAntiforgery_OnlyUnrelatedCookies_ReturnsTrue()
    {
        IRequestCookieCollection cookies = FakeCookies(
            "vord_tenant", "42",
            "preferences", "dark-mode");

        bool result = AntiforgeryStartup.ShouldSkipAntiforgery(cookies);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldSkipAntiforgery_AuthCookieAlongsideApiKeyHeader_ReturnsFalse()
    {
        // Regression check on the design choice: even if a non-cookie scheme also authenticates
        // the principal, the auth cookie being on the request means CSRF is still a viable
        // attack and the token must be verified. The skip predicate gates on cookie presence,
        // not the active identity's AuthenticationType.
        IRequestCookieCollection cookies = FakeCookies("vord_auth", "session-token");

        bool result = AntiforgeryStartup.ShouldSkipAntiforgery(cookies);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldSkipAntiforgery_NullCookies_Throws()
    {
        await Assert.That(() => AntiforgeryStartup.ShouldSkipAntiforgery((IRequestCookieCollection)null!))
            .Throws<ArgumentNullException>();
    }

    // =================== ShouldSkipAntiforgery (HttpContext overload) ===================

    [Test]
    public async Task ShouldSkipAntiforgery_HttpContextWithAuthCookie_ReturnsFalse()
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Headers.Cookie = new StringValues("vord_auth=abc");

        bool result = AntiforgeryStartup.ShouldSkipAntiforgery(ctx);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldSkipAntiforgery_HttpContextWithoutAuthCookie_ReturnsTrue()
    {
        DefaultHttpContext ctx = new();

        bool result = AntiforgeryStartup.ShouldSkipAntiforgery(ctx);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldSkipAntiforgery_NullHttpContext_Throws()
    {
        await Assert.That(() => AntiforgeryStartup.ShouldSkipAntiforgery((HttpContext)null!))
            .Throws<ArgumentNullException>();
    }

    // =================== EnableAntiforgeryIfApplicable ===================

    [Test]
    public async Task EnableAntiforgeryIfApplicable_PostEndpoint_CallsEnableAntiforgery()
    {
        EndpointDefinition definition = new(typeof(object), typeof(object), typeof(object));
        SetVerbs(definition, "POST");

        AntiforgeryStartup.EnableAntiforgeryIfApplicable(definition);

        await Assert.That(definition.AntiforgeryEnabled).IsTrue();
    }

    [Test]
    public async Task EnableAntiforgeryIfApplicable_GetEndpoint_DoesNotEnableAntiforgery()
    {
        EndpointDefinition definition = new(typeof(object), typeof(object), typeof(object));
        SetVerbs(definition, "GET");

        AntiforgeryStartup.EnableAntiforgeryIfApplicable(definition);

        await Assert.That(definition.AntiforgeryEnabled).IsFalse();
    }

    [Test]
    public async Task EnableAntiforgeryIfApplicable_PostWithSkipAttribute_DoesNotEnableAntiforgery()
    {
        EndpointDefinition definition = new(typeof(object), typeof(object), typeof(object));
        SetVerbs(definition, "POST");
        SetAttributes(definition, new SkipAntiforgeryAttribute());

        AntiforgeryStartup.EnableAntiforgeryIfApplicable(definition);

        await Assert.That(definition.AntiforgeryEnabled).IsFalse();
    }

    [Test]
    public async Task EnableAntiforgeryIfApplicable_NullEndpoint_Throws()
    {
        await Assert.That(() => AntiforgeryStartup.EnableAntiforgeryIfApplicable(null!))
            .Throws<ArgumentNullException>();
    }

    // =================== CookieAuthenticationScheme ===================

    [Test]
    public async Task CookieAuthenticationScheme_MatchesFrameworkConstant()
    {
        await Assert.That(AntiforgeryStartup.CookieAuthenticationScheme)
            .IsEqualTo(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    // =================== Test helpers ===================

    private static IRequestCookieCollection FakeCookies(params string[] keysAndValues)
    {
        Dictionary<string, string> map = new();
        for (int i = 0; (i + 1) < keysAndValues.Length; i += 2)
        {
            map[keysAndValues[i]] = keysAndValues[i + 1];
        }

        IRequestCookieCollection cookies = Substitute.For<IRequestCookieCollection>();
        cookies.ContainsKey(Arg.Any<string>())
            .Returns(call => map.ContainsKey(call.Arg<string>()));
        cookies.Keys.Returns(map.Keys);
        cookies.Count.Returns(map.Count);
        cookies[Arg.Any<string>()]
            .Returns(call => map.TryGetValue(call.Arg<string>(), out string? v) ? v : null);

        return cookies;
    }

    private static void SetVerbs(EndpointDefinition definition, params string[] verbs)
    {
        typeof(EndpointDefinition)
            .GetProperty(nameof(EndpointDefinition.Verbs))!
            .SetValue(definition, verbs);
    }

    private static void SetAttributes(EndpointDefinition definition, params object[] attributes)
    {
        typeof(EndpointDefinition)
            .GetProperty(nameof(EndpointDefinition.EndpointAttributes))!
            .SetValue(definition, attributes);
    }
}
