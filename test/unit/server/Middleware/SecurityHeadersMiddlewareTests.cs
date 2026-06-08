// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Middleware;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Test.Middleware;

/// <summary>
/// H3 tests: verifies <see cref="SecurityHeadersMiddleware"/> sets every expected security
/// header, applies the relaxed CSP only on the Hangfire dashboard path, and rejects null input.
/// </summary>
public sealed class SecurityHeadersMiddlewareTests
{
    private static HttpContext CreateContext(string path)
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Path = path;

        return ctx;
    }

    [Test]
    public async Task ApplyHeaders_AppPath_SetsStrictCsp()
    {
        HttpContext ctx = CreateContext("/v1/api/dashboard/fleet");

        SecurityHeadersMiddleware.ApplyHeaders(ctx);

        string? csp = ctx.Response.Headers["Content-Security-Policy"].ToString();
        await Assert.That(csp).IsEqualTo(SecurityHeadersMiddleware.DefaultContentSecurityPolicy);
    }

    [Test]
    public async Task ApplyHeaders_HangfirePath_SetsRelaxedCsp()
    {
        HttpContext ctx = CreateContext("/admin/hangfire");

        SecurityHeadersMiddleware.ApplyHeaders(ctx);

        string? csp = ctx.Response.Headers["Content-Security-Policy"].ToString();
        await Assert.That(csp).IsEqualTo(SecurityHeadersMiddleware.HangfireContentSecurityPolicy);
    }

    [Test]
    public async Task ApplyHeaders_HangfireSubpath_AlsoUsesRelaxedCsp()
    {
        HttpContext ctx = CreateContext("/admin/hangfire/jobs/processing");

        SecurityHeadersMiddleware.ApplyHeaders(ctx);

        string? csp = ctx.Response.Headers["Content-Security-Policy"].ToString();
        await Assert.That(csp).IsEqualTo(SecurityHeadersMiddleware.HangfireContentSecurityPolicy);
    }

    [Test]
    public async Task ApplyHeaders_SetsHsts()
    {
        HttpContext ctx = CreateContext("/anything");

        SecurityHeadersMiddleware.ApplyHeaders(ctx);

        await Assert.That(ctx.Response.Headers["Strict-Transport-Security"].ToString())
            .IsEqualTo("max-age=63072000; includeSubDomains; preload");
    }

    [Test]
    public async Task ApplyHeaders_SetsXContentTypeOptions()
    {
        HttpContext ctx = CreateContext("/anything");

        SecurityHeadersMiddleware.ApplyHeaders(ctx);

        await Assert.That(ctx.Response.Headers["X-Content-Type-Options"].ToString()).IsEqualTo("nosniff");
    }

    [Test]
    public async Task ApplyHeaders_SetsReferrerPolicy()
    {
        HttpContext ctx = CreateContext("/anything");

        SecurityHeadersMiddleware.ApplyHeaders(ctx);

        await Assert.That(ctx.Response.Headers["Referrer-Policy"].ToString()).IsEqualTo("strict-origin-when-cross-origin");
    }

    [Test]
    public async Task ApplyHeaders_SetsPermissionsPolicy()
    {
        HttpContext ctx = CreateContext("/anything");

        SecurityHeadersMiddleware.ApplyHeaders(ctx);

        await Assert.That(ctx.Response.Headers["Permissions-Policy"].ToString()).IsEqualTo("camera=(), microphone=(), geolocation=()");
    }

    [Test]
    public async Task ApplyHeaders_SetsXFrameOptionsDeny()
    {
        HttpContext ctx = CreateContext("/anything");

        SecurityHeadersMiddleware.ApplyHeaders(ctx);

        await Assert.That(ctx.Response.Headers["X-Frame-Options"].ToString()).IsEqualTo("DENY");
    }

    [Test]
    public async Task ApplyHeaders_NullContext_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            SecurityHeadersMiddleware.ApplyHeaders(null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Constructor_NullNext_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            SecurityHeadersMiddleware _ = new(null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task InvokeAsync_NullContext_Throws()
    {
        SecurityHeadersMiddleware mw = new(_ => Task.CompletedTask);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await mw.InvokeAsync(null!);
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task InvokeAsync_CallsNext()
    {
        bool nextCalled = false;
        SecurityHeadersMiddleware mw = new(_ =>
        {
            nextCalled = true;

            return Task.CompletedTask;
        });

        await mw.InvokeAsync(CreateContext("/x"));

        await Assert.That(nextCalled).IsTrue();
    }
}
