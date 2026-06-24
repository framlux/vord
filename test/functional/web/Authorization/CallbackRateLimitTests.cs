// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Net;

namespace Framlux.FleetManagement.FunctionalTest.Authorization;

/// <summary>
/// Functional tests proving the OAuth/OIDC callback paths are throttled by request count.
/// The default functional Redis fake never blocks (its script evaluator always returns 1),
/// so these tests substitute a counting Redis fake that increments per window key, allowing
/// the real <see cref="RedisFixedWindowRateLimiter"/> to trip after the configured limit.
/// The limiter is driven purely by request count — never by wall-clock time.
/// </summary>
public sealed class CallbackRateLimitTests
{
    /// <summary>
    /// Verifies that a single callback request is not rejected by the rate limiter.
    /// </summary>
    [Test]
    public async Task CallbackPath_SingleRequest_IsNotRateLimited()
    {
        using FunctionalTestFactory factory = CreateFactoryWithCountingRedis();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.GetAsync("/api/auth/callback/github");

        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// Verifies that hammering a callback path past the configured per-IP limit yields HTTP 429
    /// once the window count is exhausted, while requests within the limit do not.
    /// </summary>
    [Test]
    public async Task CallbackPath_ExceedingLimit_Returns429()
    {
        using FunctionalTestFactory factory = CreateFactoryWithCountingRedis();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // The first CallbackPermitLimit requests must all stay under the limit.
        for (int i = 0; i < RedisRateLimiterExtensions.CallbackPermitLimit; i++)
        {
            HttpResponseMessage allowed = await client.GetAsync("/api/auth/callback/github");
            await Assert.That(allowed.StatusCode).IsNotEqualTo(HttpStatusCode.TooManyRequests);
        }

        // The next request exceeds the window and must be rejected with 429.
        HttpResponseMessage limited = await client.GetAsync("/api/auth/callback/github");

        await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// Verifies that non-callback paths are unaffected by the callback limiter even when the
    /// callback window for the same IP has been exhausted.
    /// </summary>
    [Test]
    public async Task NonCallbackPath_IsNotAffectedByCallbackLimiter()
    {
        using FunctionalTestFactory factory = CreateFactoryWithCountingRedis();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // Exhaust the callback window for this IP.
        for (int i = 0; i < RedisRateLimiterExtensions.CallbackPermitLimit + 2; i++)
        {
            await client.GetAsync("/api/auth/callback/github");
        }

        HttpResponseMessage response = await client.GetAsync("/api/v1/contact");

        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// Builds a functional factory whose Redis connection counts increments per key, so the
    /// callback fixed-window limiter blocks after its permit limit is reached.
    /// </summary>
    private static FunctionalTestFactory CreateFactoryWithCountingRedis()
    {
        FunctionalTestFactory factory = new()
        {
            AdditionalTestServices = services =>
            {
                services.RemoveAll<IConnectionMultiplexer>();
                services.AddSingleton(CreateCountingRedis());
            }
        };

        return factory;
    }

    /// <summary>
    /// Creates a fake <see cref="IConnectionMultiplexer"/> whose script evaluator implements the
    /// INCR semantics the <see cref="RedisFixedWindowRateLimiter"/> relies on: each evaluation
    /// returns the post-increment count for the supplied key.
    /// </summary>
    private static IConnectionMultiplexer CreateCountingRedis()
    {
        ConcurrentDictionary<string, long> counters = new();

        IDatabase db = Substitute.For<IDatabase>();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                RedisKey[] keys = callInfo.ArgAt<RedisKey[]>(1);
                string key = keys[0].ToString();
                long count = counters.AddOrUpdate(key, 1, (_, current) => current + 1);

                return RedisResult.Create((RedisValue)count);
            });

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        redis.IsConnected.Returns(true);

        return redis;
    }
}
