// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using StackExchange.Redis;
using Testcontainers.Redis;

namespace Framlux.FleetManagement.Test.Integration;

/// <summary>
/// Provides a per-class Redis container and a connected <see cref="IConnectionMultiplexer"/> so tests
/// can exercise real server-side Lua scripts and key semantics that a substitute cannot reproduce.
/// </summary>
public sealed class RedisFixture : IAsyncDisposable
{
    private RedisContainer? _container;

    /// <summary>
    /// The connection multiplexer backed by the running Redis container. Default until
    /// <see cref="InitializeAsync"/> completes.
    /// </summary>
    public IConnectionMultiplexer Connection { get; private set; } = default!;

    /// <summary>
    /// Starts the Redis container and connects the multiplexer.
    /// </summary>
    public async Task InitializeAsync()
    {
        _container = new RedisBuilder("redis:7-alpine")
            .Build();
        await _container.StartAsync();

        Connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Connection is not null)
        {
            await Connection.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
