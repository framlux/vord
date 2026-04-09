// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;
using Grpc.Core.Testing;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="GrpcRateLimitingInterceptor"/>.
/// </summary>
public class GrpcRateLimitingInterceptorTests
{
    private static (GrpcRateLimitingInterceptor interceptor, IDatabase db) CreateInterceptor()
    {
        IDatabase db = Substitute.For<IDatabase>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        NullLogger<GrpcRateLimitingInterceptor> logger = new();
        GrpcRateLimitingInterceptor interceptor = new(redis, logger);

        return (interceptor, db);
    }

    private static ServerCallContext CreateTestContext()
    {
        ServerCallContext context = TestServerCallContext.Create(
            method: "TestMethod",
            host: "localhost",
            deadline: DateTime.MaxValue,
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None,
            peer: "ipv4:127.0.0.1:12345",
            authContext: null,
            contextPropagationToken: null,
            writeHeadersFunc: (metadata) => Task.CompletedTask,
            writeOptionsGetter: () => new WriteOptions(),
            writeOptionsSetter: (writeOptions) => { });

        return context;
    }

    /// <summary>
    /// Verifies that requests under the rate limit are forwarded to the continuation.
    /// </summary>
    [Test]
    public async Task UnaryServerHandler_UnderLimit_CallsContinuation()
    {
        (GrpcRateLimitingInterceptor interceptor, IDatabase db) = CreateInterceptor();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));
        ServerCallContext context = CreateTestContext();
        bool continuationCalled = false;
        UnaryServerMethod<string, string> continuation = (req, ctx) =>
        {
            continuationCalled = true;

            return Task.FromResult("response");
        };

        string result = await interceptor.UnaryServerHandler("request", context, continuation);

        await Assert.That(continuationCalled).IsEqualTo(true);
        await Assert.That(result).IsEqualTo("response");
    }

    /// <summary>
    /// Verifies that requests over the rate limit throw a ResourceExhausted RpcException.
    /// </summary>
    [Test]
    public async Task UnaryServerHandler_OverLimit_ThrowsResourceExhausted()
    {
        (GrpcRateLimitingInterceptor interceptor, IDatabase db) = CreateInterceptor();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)101L));
        ServerCallContext context = CreateTestContext();
        UnaryServerMethod<string, string> continuation = (req, ctx) => Task.FromResult("response");

        RpcException? exception = null;
        try
        {
            await interceptor.UnaryServerHandler("request", context, continuation);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);
    }

    // ========== ServerStreamingServerHandler tests ==========

    [Test]
    public async Task ServerStreamingServerHandler_UnderLimit_CallsContinuation()
    {
        (GrpcRateLimitingInterceptor interceptor, IDatabase db) = CreateInterceptor();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));
        ServerCallContext context = CreateTestContext();
        bool continuationCalled = false;
        IServerStreamWriter<string> responseStream = Substitute.For<IServerStreamWriter<string>>();
        ServerStreamingServerMethod<string, string> continuation = (req, stream, ctx) =>
        {
            continuationCalled = true;

            return Task.CompletedTask;
        };

        await interceptor.ServerStreamingServerHandler("request", responseStream, context, continuation);

        await Assert.That(continuationCalled).IsEqualTo(true);
    }

    [Test]
    public async Task ServerStreamingServerHandler_OverLimit_ThrowsResourceExhausted()
    {
        (GrpcRateLimitingInterceptor interceptor, IDatabase db) = CreateInterceptor();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)101L));
        ServerCallContext context = CreateTestContext();
        IServerStreamWriter<string> responseStream = Substitute.For<IServerStreamWriter<string>>();
        ServerStreamingServerMethod<string, string> continuation = (req, stream, ctx) => Task.CompletedTask;

        RpcException? exception = null;
        try
        {
            await interceptor.ServerStreamingServerHandler("request", responseStream, context, continuation);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);
    }

    // ========== ClientStreamingServerHandler tests ==========

    [Test]
    public async Task ClientStreamingServerHandler_UnderLimit_CallsContinuation()
    {
        (GrpcRateLimitingInterceptor interceptor, IDatabase db) = CreateInterceptor();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));
        ServerCallContext context = CreateTestContext();
        bool continuationCalled = false;
        IAsyncStreamReader<string> requestStream = Substitute.For<IAsyncStreamReader<string>>();
        ClientStreamingServerMethod<string, string> continuation = (stream, ctx) =>
        {
            continuationCalled = true;

            return Task.FromResult("response");
        };

        string result = await interceptor.ClientStreamingServerHandler(requestStream, context, continuation);

        await Assert.That(continuationCalled).IsEqualTo(true);
        await Assert.That(result).IsEqualTo("response");
    }

    [Test]
    public async Task ClientStreamingServerHandler_OverLimit_ThrowsResourceExhausted()
    {
        (GrpcRateLimitingInterceptor interceptor, IDatabase db) = CreateInterceptor();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)101L));
        ServerCallContext context = CreateTestContext();
        IAsyncStreamReader<string> requestStream = Substitute.For<IAsyncStreamReader<string>>();
        ClientStreamingServerMethod<string, string> continuation = (stream, ctx) => Task.FromResult("response");

        RpcException? exception = null;
        try
        {
            await interceptor.ClientStreamingServerHandler(requestStream, context, continuation);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);
    }

    // ========== DuplexStreamingServerHandler tests ==========

    [Test]
    public async Task DuplexStreamingServerHandler_UnderLimit_CallsContinuation()
    {
        (GrpcRateLimitingInterceptor interceptor, IDatabase db) = CreateInterceptor();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));
        ServerCallContext context = CreateTestContext();
        bool continuationCalled = false;
        IAsyncStreamReader<string> requestStream = Substitute.For<IAsyncStreamReader<string>>();
        IServerStreamWriter<string> responseStream = Substitute.For<IServerStreamWriter<string>>();
        DuplexStreamingServerMethod<string, string> continuation = (reqStream, respStream, ctx) =>
        {
            continuationCalled = true;

            return Task.CompletedTask;
        };

        await interceptor.DuplexStreamingServerHandler(requestStream, responseStream, context, continuation);

        await Assert.That(continuationCalled).IsEqualTo(true);
    }

    [Test]
    public async Task DuplexStreamingServerHandler_OverLimit_ThrowsResourceExhausted()
    {
        (GrpcRateLimitingInterceptor interceptor, IDatabase db) = CreateInterceptor();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)101L));
        ServerCallContext context = CreateTestContext();
        IAsyncStreamReader<string> requestStream = Substitute.For<IAsyncStreamReader<string>>();
        IServerStreamWriter<string> responseStream = Substitute.For<IServerStreamWriter<string>>();
        DuplexStreamingServerMethod<string, string> continuation = (reqStream, respStream, ctx) => Task.CompletedTask;

        RpcException? exception = null;
        try
        {
            await interceptor.DuplexStreamingServerHandler(requestStream, responseStream, context, continuation);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);
    }

    // ========== ExtractPeerIp edge cases ==========

    [Test]
    public async Task UnaryServerHandler_Ipv6Peer_ExtractsCorrectly()
    {
        (GrpcRateLimitingInterceptor interceptor, IDatabase db) = CreateInterceptor();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        ServerCallContext context = TestServerCallContext.Create(
            method: "TestMethod",
            host: "localhost",
            deadline: DateTime.MaxValue,
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None,
            peer: "ipv6:[::1]:12345",
            authContext: null,
            contextPropagationToken: null,
            writeHeadersFunc: (metadata) => Task.CompletedTask,
            writeOptionsGetter: () => new WriteOptions(),
            writeOptionsSetter: (writeOptions) => { });

        bool continuationCalled = false;
        UnaryServerMethod<string, string> continuation = (req, ctx) =>
        {
            continuationCalled = true;

            return Task.FromResult("ok");
        };

        await interceptor.UnaryServerHandler("request", context, continuation);

        // The request should be allowed (under limit) — proves IP extraction didn't crash.
        await Assert.That(continuationCalled).IsEqualTo(true);
    }
}
