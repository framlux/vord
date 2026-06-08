// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Server.Middleware;

/// <summary>
/// Sets industry-standard security response headers on every response. Two CSP profiles are
/// applied: a strict default for application paths, and a relaxed profile for the Hangfire
/// dashboard route (Hangfire serves inline scripts and styles that require
/// <c>'unsafe-inline'</c>). The middleware runs after <c>UseForwardedHeaders</c> so that
/// <c>HttpContext.Connection</c> reflects the real client IP for any logging downstream.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    /// <summary>Path prefix matched against the request to apply the relaxed Hangfire CSP.</summary>
    public const string HangfireDashboardPrefix = "/admin/hangfire";

    /// <summary>Default strict CSP applied to all non-Hangfire paths.</summary>
    public const string DefaultContentSecurityPolicy =
        "default-src 'self'; "
        + "base-uri 'self'; "
        + "frame-ancestors 'none'; "
        + "img-src 'self' data:; "
        + "style-src 'self' 'unsafe-inline'; "
        + "script-src 'self'; "
        + "connect-src 'self'; "
        + "object-src 'none'";

    /// <summary>
    /// Relaxed CSP applied to the Hangfire dashboard subtree. Hangfire 1.8 ships inline scripts
    /// and inline styles in its bundled UI; <c>'unsafe-inline'</c> is necessary for the
    /// dashboard to render. The dashboard is admin-only via
    /// <see cref="Framlux.FleetManagement.Services.Core.Hangfire.HangfireDashboardAuthorizationFilter"/>.
    /// </summary>
    public const string HangfireContentSecurityPolicy =
        "default-src 'self'; "
        + "base-uri 'self'; "
        + "frame-ancestors 'none'; "
        + "img-src 'self' data:; "
        + "style-src 'self' 'unsafe-inline'; "
        + "script-src 'self' 'unsafe-inline' 'unsafe-eval'; "
        + "connect-src 'self'; "
        + "object-src 'none'";

    private readonly RequestDelegate _next;

    /// <summary>
    /// Creates a new instance of the <see cref="SecurityHeadersMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>
    /// Adds security headers to the response. Headers are written via
    /// <see cref="HttpResponse.OnStarting(Func{Task})"/> so any later middleware that mutates
    /// the response headers (e.g., FastEndpoints' content-type setter) does not race the
    /// security-header writes.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(() =>
        {
            ApplyHeaders(context);

            return Task.CompletedTask;
        });

        return _next(context);
    }

    /// <summary>
    /// Pure header-application logic. Exposed as an <c>internal static</c> method so it can be
    /// unit tested against a plain <see cref="HttpContext"/> without running the middleware
    /// pipeline. The same logic determines which CSP variant to apply.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    internal static void ApplyHeaders(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IHeaderDictionary headers = context.Response.Headers;

        // CSP: Hangfire path gets the relaxed profile; everything else gets the strict default.
        bool isHangfirePath = context.Request.Path.StartsWithSegments(HangfireDashboardPrefix);
        string csp = isHangfirePath ? HangfireContentSecurityPolicy : DefaultContentSecurityPolicy;
        headers["Content-Security-Policy"] = csp;

        // 2 years (per HSTS preload-list rules), includeSubDomains, preload.
        headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains; preload";

        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["X-Frame-Options"] = "DENY";
    }
}
