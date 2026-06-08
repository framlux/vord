// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Server.Startup;

/// <summary>
/// Centralizes the CSRF/antiforgery wiring for the FastEndpoints surface: service registration,
/// the FE endpoint configurator that opts each state-changing endpoint into the middleware, and
/// the runtime skip-request predicate that bypasses non-cookie callers. Each piece is exposed as
/// a static method so it can be unit-tested without a running web host.
/// </summary>
public static class AntiforgeryStartup
{
    /// <summary>
    /// Name of the auth cookie issued by the cookie authentication handler. The skip predicate
    /// looks for this cookie on the incoming request — its presence is the precise gate for CSRF
    /// being a viable attack against this request, regardless of which scheme ultimately
    /// authenticated the principal.
    /// </summary>
    public const string AuthCookieName = "vord_auth";

    /// <summary>
    /// Name of the antiforgery cookie issued by ASP.NET Core's antiforgery service.
    /// </summary>
    public const string AntiforgeryCookieName = "vord_csrf";

    /// <summary>
    /// Name of the request header carrying the antiforgery token in JSON / fetch flows.
    /// </summary>
    public const string AntiforgeryHeaderName = "X-CSRF-TOKEN";

    /// <summary>
    /// Configures <see cref="AntiforgeryOptions"/> for the production server.
    /// <para>
    /// <see cref="CookieSecurePolicy.Always"/> means the antiforgery cookie is only set when the
    /// browser request was HTTPS. In production the Traefik proxy terminates TLS and the
    /// ForwardedHeaders middleware reports <c>Request.Scheme = "https"</c>, so the Secure flag
    /// is always applied. Functional tests run over HTTP — <c>FunctionalTestFactory</c> overrides
    /// this option to <see cref="CookieSecurePolicy.SameAsRequest"/> so the test environment can
    /// exercise the antiforgery flow without weakening production posture.
    /// </para>
    /// <para>
    /// <see cref="SameSiteMode.Strict"/> on the antiforgery cookie prevents browsers from sending
    /// it on cross-site top-level navigation. A malicious site's form POST that rides the user's
    /// auth cookie therefore arrives without the antiforgery cookie pair and is rejected by the
    /// middleware.
    /// </para>
    /// </summary>
    /// <param name="options">The framework options to populate.</param>
    public static void ConfigureOptions(AntiforgeryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.HeaderName = AntiforgeryHeaderName;
        options.Cookie.Name = AntiforgeryCookieName;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
    }

    /// <summary>
    /// Endpoint configurator invoked by FastEndpoints once per registered endpoint at startup.
    /// Opts the endpoint into antiforgery enforcement when
    /// <see cref="AntiforgeryEnrollment.ShouldEnforce(EndpointDefinition)"/> says yes — that is,
    /// when the endpoint is state-changing (verb other than GET/HEAD/OPTIONS) and does not carry
    /// <see cref="SkipAntiforgeryAttribute"/>.
    /// </summary>
    /// <param name="endpoint">The endpoint definition being configured.</param>
    public static void EnableAntiforgeryIfApplicable(EndpointDefinition endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (AntiforgeryEnrollment.ShouldEnforce(endpoint))
        {
            endpoint.EnableAntiforgery();
        }
    }

    /// <summary>
    /// Predicate passed to <c>UseAntiforgeryFE(skipRequestFilter: ...)</c>. Returns <c>true</c>
    /// when antiforgery enforcement should be skipped for this request — specifically when the
    /// request did not carry the auth cookie. CSRF requires the browser to attach a session
    /// cookie automatically; an API-key or anonymous caller has no session cookie and therefore
    /// no CSRF surface, so requiring an antiforgery token there would block legitimate calls
    /// without adding security.
    /// <para>
    /// We deliberately key on the presence of the cookie rather than the authenticated
    /// identity's <c>AuthenticationType</c>: with a multi-scheme policy the primary identity may
    /// be a non-cookie scheme (e.g., API key) even when the cookie is also present on the
    /// request, and that scenario should still be CSRF-protected.
    /// </para>
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <returns><c>true</c> to skip the antiforgery check for this request.</returns>
    public static bool ShouldSkipAntiforgery(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return ShouldSkipAntiforgery(httpContext.Request.Cookies);
    }

    /// <summary>
    /// Testable overload of <see cref="ShouldSkipAntiforgery(HttpContext)"/> that operates
    /// directly on the request cookie collection so the rule can be exercised without
    /// constructing an <see cref="HttpContext"/>.
    /// </summary>
    /// <param name="requestCookies">The cookies attached to the inbound request.</param>
    /// <returns><c>true</c> to skip the antiforgery check for this request.</returns>
    public static bool ShouldSkipAntiforgery(IRequestCookieCollection requestCookies)
    {
        ArgumentNullException.ThrowIfNull(requestCookies);

        return requestCookies.ContainsKey(AuthCookieName) == false;
    }

    /// <summary>
    /// Static reference to the cookie auth scheme name. Exposed so callers can match the
    /// project's chosen scheme without importing <c>Microsoft.AspNetCore.Authentication.Cookies</c>
    /// directly. Currently unused outside this file, but kept for symmetry with the other
    /// constants surfaced here.
    /// </summary>
    public static string CookieAuthenticationScheme => CookieAuthenticationDefaults.AuthenticationScheme;
}
