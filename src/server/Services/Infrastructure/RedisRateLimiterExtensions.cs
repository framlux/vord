// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Threading.RateLimiting;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Extension methods for configuring Redis-backed rate limiting.
/// </summary>
public static class RedisRateLimiterExtensions
{
    /// <summary>
    /// Redis key prefix for the OAuth/OIDC callback rate limiter.
    /// </summary>
    public const string CallbackKeyPrefix = "ratelimit:callback";

    /// <summary>
    /// Maximum number of callback requests permitted per IP within
    /// <see cref="CallbackWindow"/>. Callbacks are a sensitive surface (correlation/nonce
    /// guessing, token replay), so the limit is strict.
    /// </summary>
    public const int CallbackPermitLimit = 10;

    /// <summary>
    /// The fixed window over which <see cref="CallbackPermitLimit"/> callback requests
    /// are counted per IP.
    /// </summary>
    public static readonly TimeSpan CallbackWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates the Redis-backed fixed-window limiter used for OAuth/OIDC callbacks. The
    /// named <c>callback</c> policy and <c>CallbackRateLimitMiddleware</c> share this
    /// factory so they enforce identical limits.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="logger">Optional logger used to warn when the limiter fails open on a Redis outage.</param>
    /// <returns>A fixed-window limiter configured for the callback surface.</returns>
    public static RedisFixedWindowRateLimiter CreateCallbackLimiter(IConnectionMultiplexer redis, ILogger? logger = null)
    {
        return new RedisFixedWindowRateLimiter(redis, CallbackKeyPrefix, CallbackPermitLimit, CallbackWindow, logger);
    }

    /// <summary>
    /// Configures Redis-backed rate limiting for the application, replacing in-memory rate limiting
    /// so counters are shared across Kubernetes replicas. The <see cref="IConnectionMultiplexer"/>
    /// is resolved lazily from DI, allowing tests to replace it before any connection is established.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedisRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        services.AddSingleton<IConfigureOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>>(sp =>
        {
            IConnectionMultiplexer redis = sp.GetRequiredService<IConnectionMultiplexer>();
            ILogger limiterLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(RedisFixedWindowRateLimiter.MeterName);

            return new ConfigureOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(options =>
            {
                RedisFixedWindowRateLimiter globalLimiter = new(redis, "ratelimit:global", 100, TimeSpan.FromMinutes(1), limiterLogger);
                RedisFixedWindowRateLimiter loginLimiter = new(redis, "ratelimit:login", 10, TimeSpan.FromMinutes(5), limiterLogger);
                // Dedicated policy for anonymous token-authenticated endpoints (data-export
                // download). 30/min per IP — tighter than global because a single IP brute-forcing
                // 64-hex tokens is cheap to mount and the only response we can offer is to slow them.
                RedisFixedWindowRateLimiter anonymousTokenLimiter = new(redis, "ratelimit:anonymous-token", 30, TimeSpan.FromMinutes(1), limiterLogger);
                // Strict policy for the OAuth/OIDC callback paths. Note this named "callback"
                // policy does NOT enforce the callback limit: the OAuth/OIDC callbacks are
                // authentication-scheme middleware mappings, not FastEndpoints, so nothing can
                // attach RequireRateLimiting("callback") to them. Actual enforcement is done by
                // CallbackRateLimitMiddleware (which shares the identical limit via
                // CreateCallbackLimiter). The policy is registered only for parity and
                // observability, so the callback surface appears alongside the other named
                // policies; do not assume it throttles the callbacks on its own.
                RedisFixedWindowRateLimiter callbackLimiter = CreateCallbackLimiter(redis, limiterLogger);

                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    string partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                    return RateLimitPartition.Get(partitionKey, key =>
                        new RedisPartitionedRateLimiter(globalLimiter, key));
                });

                options.AddPolicy("login", context =>
                {
                    string partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                    return RateLimitPartition.Get(partitionKey, key =>
                        new RedisPartitionedRateLimiter(loginLimiter, key));
                });

                options.AddPolicy("anonymous-token", context =>
                {
                    string partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                    return RateLimitPartition.Get(partitionKey, key =>
                        new RedisPartitionedRateLimiter(anonymousTokenLimiter, key));
                });

                options.AddPolicy("callback", context =>
                {
                    string partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                    return RateLimitPartition.Get(partitionKey, key =>
                        new RedisPartitionedRateLimiter(callbackLimiter, key));
                });
            });
        });

        return services;
    }
}
