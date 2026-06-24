// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using System.Net;

namespace Framlux.FleetManagement.Test.Middleware;

/// <summary>
/// Tests for <see cref="CallbackRateLimitMiddleware"/>, focusing on the fail-open posture:
/// a transient Redis/limiter failure must not break logins, so the request is allowed through
/// rather than returning HTTP 429 or surfacing the exception as a 500.
/// </summary>
public sealed class CallbackRateLimitMiddlewareTests
{
    /// <summary>
    /// Verifies that when the Redis-backed limiter throws (simulating a Redis outage), the
    /// middleware fails open: the next middleware runs and the response is neither 429 nor an
    /// error status, so OAuth callbacks keep working during a transient Redis blip.
    /// </summary>
    [Test]
    public async Task InvokeAsync_LimiterThrows_FailsOpenAndCallsNext()
    {
        bool nextCalled = false;
        Task Next(HttpContext _)
        {
            nextCalled = true;

            return Task.CompletedTask;
        }

        IConnectionMultiplexer redis = CreateThrowingRedis();
        ILogger<CallbackRateLimitMiddleware> logger = Substitute.For<ILogger<CallbackRateLimitMiddleware>>();
        CallbackRateLimitMiddleware middleware = new(Next, redis, logger);

        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        await middleware.InvokeAsync(context);

        await Assert.That(nextCalled).IsTrue();
        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
        await Assert.That(context.Response.StatusCode).IsNotEqualTo(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Verifies that a healthy limiter under its window allows the request and invokes the next
    /// middleware without setting a 429 status.
    /// </summary>
    [Test]
    public async Task InvokeAsync_UnderLimit_CallsNext()
    {
        bool nextCalled = false;
        Task Next(HttpContext _)
        {
            nextCalled = true;

            return Task.CompletedTask;
        }

        IConnectionMultiplexer redis = CreateCountingRedis(returnCount: 1L);
        ILogger<CallbackRateLimitMiddleware> logger = Substitute.For<ILogger<CallbackRateLimitMiddleware>>();
        CallbackRateLimitMiddleware middleware = new(Next, redis, logger);

        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.8");

        await middleware.InvokeAsync(context);

        await Assert.That(nextCalled).IsTrue();
        await Assert.That(context.Response.StatusCode).IsNotEqualTo(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Verifies that when the limiter reports the window is exhausted the middleware short-circuits
    /// with HTTP 429 and does not invoke the next middleware.
    /// </summary>
    [Test]
    public async Task InvokeAsync_OverLimit_Returns429AndDoesNotCallNext()
    {
        bool nextCalled = false;
        Task Next(HttpContext _)
        {
            nextCalled = true;

            return Task.CompletedTask;
        }

        IConnectionMultiplexer redis = CreateCountingRedis(returnCount: long.MaxValue);
        ILogger<CallbackRateLimitMiddleware> logger = Substitute.For<ILogger<CallbackRateLimitMiddleware>>();
        CallbackRateLimitMiddleware middleware = new(Next, redis, logger);

        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

        await middleware.InvokeAsync(context);

        await Assert.That(nextCalled).IsFalse();
        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Creates a fake <see cref="IConnectionMultiplexer"/> whose script evaluator throws a
    /// <see cref="RedisConnectionException"/>, simulating an unreachable Redis.
    /// </summary>
    private static IConnectionMultiplexer CreateThrowingRedis()
    {
        IDatabase db = Substitute.For<IDatabase>();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns<RedisResult>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Connection refused"));

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        return redis;
    }

    /// <summary>
    /// Creates a fake <see cref="IConnectionMultiplexer"/> whose script evaluator returns the
    /// supplied post-increment count, mirroring the INCR semantics the limiter relies on.
    /// </summary>
    private static IConnectionMultiplexer CreateCountingRedis(long returnCount)
    {
        IDatabase db = Substitute.For<IDatabase>();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)returnCount));

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        return redis;
    }
}
