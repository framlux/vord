// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

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

            return new ConfigureOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(options =>
            {
                RedisFixedWindowRateLimiter globalLimiter = new(redis, "ratelimit:global", 100, TimeSpan.FromMinutes(1));
                RedisFixedWindowRateLimiter loginLimiter = new(redis, "ratelimit:login", 10, TimeSpan.FromMinutes(5));

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
            });
        });

        return services;
    }
}
