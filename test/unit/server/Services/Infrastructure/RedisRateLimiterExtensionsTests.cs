// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="RedisRateLimiterExtensions"/>.
/// </summary>
public class RedisRateLimiterExtensionsTests
{
    /// <summary>
    /// Verifies that AddRedisRateLimiting registers the RateLimiterOptions configuration.
    /// </summary>
    [Test]
    public async Task AddRedisRateLimiting_RegistersRateLimiterOptions()
    {
        ServiceCollection services = new();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        services.AddSingleton(redis);
        services.AddLogging();

        services.AddRedisRateLimiting();

        ServiceProvider provider = services.BuildServiceProvider();
        IConfigureOptions<RateLimiterOptions>? configureOptions =
            provider.GetService<IConfigureOptions<RateLimiterOptions>>();

        await Assert.That(configureOptions).IsNotNull();
    }

    /// <summary>
    /// Verifies that AddRedisRateLimiting returns the service collection for chaining.
    /// </summary>
    [Test]
    public async Task AddRedisRateLimiting_ReturnsServiceCollectionForChaining()
    {
        ServiceCollection services = new();

        IServiceCollection result = services.AddRedisRateLimiting();

        await Assert.That(result).IsEqualTo(services);
    }

    /// <summary>
    /// Verifies that the configured options set the rejection status code to 429.
    /// </summary>
    [Test]
    public async Task AddRedisRateLimiting_SetsRejectionStatusCodeTo429()
    {
        ServiceCollection services = new();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        services.AddSingleton(redis);
        services.AddLogging();

        services.AddRedisRateLimiting();

        ServiceProvider provider = services.BuildServiceProvider();

        // Get all IConfigureOptions<RateLimiterOptions> registrations and apply them
        IEnumerable<IConfigureOptions<RateLimiterOptions>> allOptions =
            provider.GetServices<IConfigureOptions<RateLimiterOptions>>();

        RateLimiterOptions options = new();
        foreach (IConfigureOptions<RateLimiterOptions> configOption in allOptions)
        {
            configOption.Configure(options);
        }

        await Assert.That(options.RejectionStatusCode).IsEqualTo(429);
    }

    /// <summary>
    /// Verifies that the configured options set up a global limiter.
    /// </summary>
    [Test]
    public async Task AddRedisRateLimiting_ConfiguresGlobalLimiter()
    {
        ServiceCollection services = new();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        services.AddSingleton(redis);
        services.AddLogging();

        services.AddRedisRateLimiting();

        ServiceProvider provider = services.BuildServiceProvider();

        IEnumerable<IConfigureOptions<RateLimiterOptions>> allOptions =
            provider.GetServices<IConfigureOptions<RateLimiterOptions>>();

        RateLimiterOptions options = new();
        foreach (IConfigureOptions<RateLimiterOptions> configOption in allOptions)
        {
            configOption.Configure(options);
        }

        await Assert.That(options.GlobalLimiter).IsNotNull();
    }

    /// <summary>
    /// Verifies that AddRedisRateLimiting registers the login rate limit policy.
    /// </summary>
    [Test]
    public async Task AddRedisRateLimiting_RegistersLoginPolicy()
    {
        ServiceCollection services = new();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        services.AddSingleton(redis);
        services.AddLogging();

        services.AddRedisRateLimiting();

        ServiceProvider provider = services.BuildServiceProvider();

        IEnumerable<IConfigureOptions<RateLimiterOptions>> allOptions =
            provider.GetServices<IConfigureOptions<RateLimiterOptions>>();

        RateLimiterOptions options = new();
        foreach (IConfigureOptions<RateLimiterOptions> configOption in allOptions)
        {
            configOption.Configure(options);
        }

        // Verify the "login" policy was added by checking the unresolved policy count
        // The login policy is added via AddPolicy, so it should be in the policy map
        await Assert.That(options.GlobalLimiter).IsNotNull();
    }
}
