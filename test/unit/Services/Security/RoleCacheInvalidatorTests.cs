// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Security;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Security;

/// <summary>
/// Unit tests for <see cref="RoleCacheInvalidator"/>.
/// Verifies that the correct Redis key is deleted when invalidating a user's role cache.
/// </summary>
public sealed class RoleCacheInvalidatorTests
{
    private static (RoleCacheInvalidator invalidator, IDatabase redisDb) CreateInvalidator()
    {
        IDatabase redisDb = Substitute.For<IDatabase>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        RoleCacheInvalidator invalidator = new(redis);

        return (invalidator, redisDb);
    }

    [Test]
    public async Task InvalidateAsync_DeletesCorrectRedisKey()
    {
        (RoleCacheInvalidator invalidator, IDatabase redisDb) = CreateInvalidator();
        int userId = 42;

        await invalidator.InvalidateAsync(userId, CancellationToken.None);

        string expectedKey = $"{CookiePrincipalValidator.RoleCacheKeyPrefix}{userId}";
        await redisDb.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == expectedKey),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task InvalidateAsync_DifferentUserIds_DeleteDifferentKeys()
    {
        (RoleCacheInvalidator invalidator, IDatabase redisDb) = CreateInvalidator();

        await invalidator.InvalidateAsync(1, CancellationToken.None);
        await invalidator.InvalidateAsync(999, CancellationToken.None);

        string expectedKey1 = $"{CookiePrincipalValidator.RoleCacheKeyPrefix}1";
        string expectedKey2 = $"{CookiePrincipalValidator.RoleCacheKeyPrefix}999";

        await redisDb.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == expectedKey1),
            Arg.Any<CommandFlags>());
        await redisDb.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == expectedKey2),
            Arg.Any<CommandFlags>());
    }

}
