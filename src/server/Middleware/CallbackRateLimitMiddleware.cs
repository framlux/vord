// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;

namespace Framlux.FleetManagement.Server.Middleware;

/// <summary>
/// Enforces a strict per-IP rate limit on the OAuth/OIDC callback paths
/// (<c>/api/auth/callback/{github,google,microsoft,tenant-oidc}</c>). These callbacks are
/// authentication-scheme middleware mappings, not FastEndpoints, so they cannot use the
/// endpoint-level <c>RequireRateLimiting</c>. The check is applied here as a path-scoped
/// branch acquiring a lease from a Redis-backed fixed-window limiter so the count holds
/// across replicas. When the lease is denied the request is short-circuited with HTTP 429.
/// </summary>
public sealed class CallbackRateLimitMiddleware
{
    /// <summary>
    /// The path prefix the callback rate limit applies to. Every OAuth/OIDC scheme's
    /// <c>CallbackPath</c> is mounted under this prefix.
    /// </summary>
    public const string CallbackPathPrefix = "/api/auth/callback";

    private readonly RequestDelegate _next;
    private readonly RedisFixedWindowRateLimiter _limiter;
    private readonly ILogger<CallbackRateLimitMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CallbackRateLimitMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="redis">The Redis connection multiplexer backing the limiter.</param>
    /// <param name="logger">The logger used to record fail-open events.</param>
    public CallbackRateLimitMiddleware(RequestDelegate next, IConnectionMultiplexer redis, ILogger<CallbackRateLimitMiddleware> logger)
    {
        _next = next;
        _limiter = RedisRateLimiterExtensions.CreateCallbackLimiter(redis, logger);
        _logger = logger;
    }

    /// <summary>
    /// Applies the callback rate limit to requests under <see cref="CallbackPathPrefix"/>,
    /// returning HTTP 429 when the per-IP window is exhausted and otherwise passing the
    /// request to the next middleware. If the limiter check itself fails (for example a
    /// transient Redis outage) the middleware fails open: it logs a warning and allows the
    /// request through so an anti-abuse limiter blip cannot take down all logins.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        string partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        bool allowed;
        try
        {
            allowed = await _limiter.IsAllowedAsync(partitionKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Callback rate-limit check failed; allowing request");
            await _next(context);

            return;
        }

        if (allowed == false)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

            return;
        }

        await _next(context);
    }
}
