// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Auth;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="OidcNonceValidator"/> nonce matching and single-use replay protection.
/// </summary>
public sealed class OidcNonceValidatorTests
{
    private static IDatabase CreateRedisDb(bool firstUse = true)
    {
        IDatabase redisDb = Substitute.For<IDatabase>();

        // StringSetAsync with When.NotExists returns true the first time, false on replay.
        redisDb.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), When.NotExists, Arg.Any<CommandFlags>())
            .Returns(firstUse);

        return redisDb;
    }

    [Test]
    public async Task ValidateNonce_MatchingNonce_FirstUse_ReturnsTrue()
    {
        IDatabase redisDb = CreateRedisDb(firstUse: true);
        bool result = await OidcNonceValidator.ValidateAndConsumeAsync(redisDb, cookieNonce: "abc", tokenNonce: "abc");
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ValidateNonce_MismatchedNonce_ReturnsFalse()
    {
        IDatabase redisDb = CreateRedisDb(firstUse: true);
        bool result = await OidcNonceValidator.ValidateAndConsumeAsync(redisDb, cookieNonce: "abc", tokenNonce: "different");
        await Assert.That(result).IsFalse();
        await redisDb.DidNotReceive().StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task ValidateNonce_MissingNonce_ReturnsFalse()
    {
        IDatabase redisDb = CreateRedisDb(firstUse: true);
        bool result = await OidcNonceValidator.ValidateAndConsumeAsync(redisDb, cookieNonce: null, tokenNonce: "abc");
        await Assert.That(result).IsFalse();
        await redisDb.DidNotReceive().StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task ValidateNonce_MissingTokenNonce_ReturnsFalse()
    {
        IDatabase redisDb = CreateRedisDb(firstUse: true);
        bool result = await OidcNonceValidator.ValidateAndConsumeAsync(redisDb, cookieNonce: "abc", tokenNonce: null);
        await Assert.That(result).IsFalse();
        await redisDb.DidNotReceive().StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task ValidateNonce_Replay_ReturnsFalse()
    {
        IDatabase redisDb = CreateRedisDb(firstUse: false);
        bool result = await OidcNonceValidator.ValidateAndConsumeAsync(redisDb, cookieNonce: "abc", tokenNonce: "abc");
        await Assert.That(result).IsFalse();
    }
}
