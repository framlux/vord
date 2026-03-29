// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Grpc.Core.Interceptors;
using Grpc.Core;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// gRPC server interceptor that enforces Redis-backed rate limiting on all gRPC calls.
/// Uses the same 100 req/min window as the HTTP global rate limiter.
/// </summary>
public sealed class GrpcRateLimitingInterceptor : Interceptor
{
    private readonly RedisFixedWindowRateLimiter _limiter;
    private readonly ILogger<GrpcRateLimitingInterceptor> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="GrpcRateLimitingInterceptor"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="logger">The logger instance.</param>
    public GrpcRateLimitingInterceptor(IConnectionMultiplexer redis, ILogger<GrpcRateLimitingInterceptor> logger)
    {
        _limiter = new RedisFixedWindowRateLimiter(redis, "ratelimit:grpc", 100, TimeSpan.FromMinutes(1));
        _logger = logger;
    }

    /// <inheritdoc/>
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        string peerIp = ExtractPeerIp(context);
        if (await _limiter.IsAllowedAsync(peerIp) == false)
        {
            _logger.LogWarning("gRPC rate limit exceeded for peer {PeerIp} on method {Method}", peerIp, context.Method);
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Rate limit exceeded"));
        }

        return await continuation(request, context);
    }

    /// <inheritdoc/>
    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        string peerIp = ExtractPeerIp(context);
        if (await _limiter.IsAllowedAsync(peerIp) == false)
        {
            _logger.LogWarning("gRPC rate limit exceeded for peer {PeerIp} on method {Method}", peerIp, context.Method);
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Rate limit exceeded"));
        }

        await continuation(request, responseStream, context);
    }

    /// <inheritdoc/>
    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        string peerIp = ExtractPeerIp(context);
        if (await _limiter.IsAllowedAsync(peerIp) == false)
        {
            _logger.LogWarning("gRPC rate limit exceeded for peer {PeerIp} on method {Method}", peerIp, context.Method);
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Rate limit exceeded"));
        }

        return await continuation(requestStream, context);
    }

    /// <inheritdoc/>
    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        string peerIp = ExtractPeerIp(context);
        if (await _limiter.IsAllowedAsync(peerIp) == false)
        {
            _logger.LogWarning("gRPC rate limit exceeded for peer {PeerIp} on method {Method}", peerIp, context.Method);
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Rate limit exceeded"));
        }

        await continuation(requestStream, responseStream, context);
    }

    private static string ExtractPeerIp(ServerCallContext context)
    {
        string? peer = context.Peer;
        if (string.IsNullOrEmpty(peer) == false)
        {
            // Peer format is typically "ipv4:host:port" or "ipv6:[host]:port"
            int lastColon = peer.LastIndexOf(':');
            if (lastColon > 0)
            {
                string hostPart = peer[..lastColon];

                // Strip protocol prefix (e.g., "ipv4:" or "ipv6:")
                int protocolEnd = hostPart.IndexOf(':');
                if ((protocolEnd > 0) && (protocolEnd < hostPart.Length - 1))
                {
                    return hostPart[(protocolEnd + 1)..];
                }

                return hostPart;
            }
        }

        // Fallback to HttpContext connection info
        return context.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
