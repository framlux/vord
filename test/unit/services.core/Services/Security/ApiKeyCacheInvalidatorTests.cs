// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Security;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Security;

/// <summary>
/// Unit tests for <see cref="ApiKeyCacheInvalidator"/>. Verifies the correct Redis key is deleted
/// when invalidating a machine's API key auth cache by its stored hash.
/// </summary>
public sealed class ApiKeyCacheInvalidatorTests
{
    private static (ApiKeyCacheInvalidator invalidator, IDatabase redisDb, ILogger<ApiKeyCacheInvalidator> logger) CreateInvalidator()
    {
        IDatabase redisDb = Substitute.For<IDatabase>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        ILogger<ApiKeyCacheInvalidator> logger = Substitute.For<ILogger<ApiKeyCacheInvalidator>>();

        ApiKeyCacheInvalidator invalidator = new(redis, logger);

        return (invalidator, redisDb, logger);
    }

    [Test]
    public async Task InvalidateByHashAsync_DeletesKeyUnderSharedPrefix()
    {
        (ApiKeyCacheInvalidator invalidator, IDatabase redisDb, _) = CreateInvalidator();
        const string keyHash = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef0";

        await invalidator.InvalidateByHashAsync(keyHash, CancellationToken.None);

        string expectedKey = $"{IApiKeyCacheInvalidator.CacheKeyPrefix}{keyHash}";
        await redisDb.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == expectedKey),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task InvalidateByHashAsync_UsesSameApikeyPrefixAsHandler()
    {
        (ApiKeyCacheInvalidator invalidator, IDatabase redisDb, _) = CreateInvalidator();

        await invalidator.InvalidateByHashAsync("abc123", CancellationToken.None);

        // The literal prefix must stay "apikey:" so it matches the key the auth handler writes.
        await redisDb.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "apikey:abc123"),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task InvalidateByHashAsync_NullOrWhitespace_Throws()
    {
        (ApiKeyCacheInvalidator invalidator, _, _) = CreateInvalidator();

        await Assert.That(async () => await invalidator.InvalidateByHashAsync(null!, CancellationToken.None))
            .Throws<ArgumentException>();
        await Assert.That(async () => await invalidator.InvalidateByHashAsync("   ", CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task InvalidateByHashAsync_RedisException_DoesNotThrow()
    {
        (ApiKeyCacheInvalidator invalidator, IDatabase redisDb, _) = CreateInvalidator();
        redisDb.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<bool>(callInfo => throw new RedisException("Delete failed"));

        // A Redis fault must be swallowed so the surrounding delete/reissue still reports success.
        await invalidator.InvalidateByHashAsync("somehash", CancellationToken.None);
    }

    [Test]
    public async Task InvalidateByHashAsync_RedisException_LogsWarning()
    {
        (ApiKeyCacheInvalidator invalidator, IDatabase redisDb, ILogger<ApiKeyCacheInvalidator> logger) = CreateInvalidator();
        redisDb.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<bool>(callInfo => throw new RedisException("Delete failed"));

        await invalidator.InvalidateByHashAsync("somehash", CancellationToken.None);

        // The security path must surface Redis faults for operational visibility, matching the
        // auth handler's behaviour rather than failing silently.
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<RedisException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task Constructor_NullRedis_Throws()
    {
        ILogger<ApiKeyCacheInvalidator> logger = Substitute.For<ILogger<ApiKeyCacheInvalidator>>();

        await Assert.That(() => new ApiKeyCacheInvalidator(null!, logger)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();

        await Assert.That(() => new ApiKeyCacheInvalidator(redis, null!)).Throws<ArgumentNullException>();
    }
}
