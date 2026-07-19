// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using System.Collections;
using System.Reflection;

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

        await Assert.That(GetPolicyNames(options)).Contains("login");
    }

    /// <summary>
    /// Verifies that <see cref="RedisRateLimiterExtensions.CreateCallbackLimiter"/> builds a
    /// strict limiter that blocks once its permit count is exceeded within the window. The
    /// limiter is driven by request count, never by wall-clock time.
    /// </summary>
    [Test]
    public async Task CreateCallbackLimiter_BlocksAfterPermitLimit()
    {
        long count = 0;
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(_ => RedisResult.Create((RedisValue)System.Threading.Interlocked.Increment(ref count)));
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        RedisFixedWindowRateLimiter limiter = RedisRateLimiterExtensions.CreateCallbackLimiter(redis);

        for (int i = 0; i < RedisRateLimiterExtensions.CallbackPermitLimit; i++)
        {
            bool allowed = await limiter.IsAllowedAsync("198.51.100.7");
            await Assert.That(allowed).IsTrue();
        }

        bool blocked = await limiter.IsAllowedAsync("198.51.100.7");

        await Assert.That(blocked).IsFalse();
    }

    /// <summary>
    /// Reads the names of the policies registered on the supplied options. The policy map is
    /// an internal framework dictionary, so it is read via reflection.
    /// </summary>
    private static IReadOnlyCollection<string> GetPolicyNames(RateLimiterOptions options)
    {
        PropertyInfo policyMapProperty = typeof(RateLimiterOptions).GetProperty(
            "PolicyMap",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("RateLimiterOptions.PolicyMap was not found.");

        object? policyMap = policyMapProperty.GetValue(options);
        if (policyMap is not IDictionary dictionary)
        {
            throw new InvalidOperationException("RateLimiterOptions.PolicyMap was not a dictionary.");
        }

        List<string> names = new();
        foreach (object? key in dictionary.Keys)
        {
            if (key is string name)
            {
                names.Add(name);
            }
        }

        return names;
    }
}
