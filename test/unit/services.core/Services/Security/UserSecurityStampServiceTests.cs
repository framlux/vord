// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Security;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Security;

/// <summary>
/// Tests for <see cref="UserSecurityStampService"/>: a missing stamp is minted on read,
/// and bumping writes a new value so old cookies no longer match.
/// </summary>
public sealed class UserSecurityStampServiceTests
{
    private static IConnectionMultiplexer CreateRedis(IDatabase db)
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        return redis;
    }

    [Test]
    public async Task GetCurrentStampAsync_NoStamp_MintsAndReturnsNonEmptyValue()
    {
        IDatabase db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        UserSecurityStampService service = new(CreateRedis(db));

        string stamp = await service.GetCurrentStampAsync(42, CancellationToken.None);

        await Assert.That(stamp).IsNotEqualTo(string.Empty);
        await db.Received(1).StringSetAsync(
            (RedisKey)"user:stamp:42",
            Arg.Is<RedisValue>(v => v.HasValue && ((string?)v)!.Length > 0),
            Arg.Any<TimeSpan?>(), Arg.Any<bool>(), When.NotExists, Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task GetCurrentStampAsync_ExistingStamp_ReturnsStoredValue()
    {
        IDatabase db = Substitute.For<IDatabase>();
        db.StringGetAsync((RedisKey)"user:stamp:42", Arg.Any<CommandFlags>()).Returns((RedisValue)"deadbeef");
        UserSecurityStampService service = new(CreateRedis(db));

        string stamp = await service.GetCurrentStampAsync(42, CancellationToken.None);

        await Assert.That(stamp).IsEqualTo("deadbeef");
    }

    [Test]
    public async Task BumpAsync_WritesNonEmptyValue()
    {
        IDatabase db = Substitute.For<IDatabase>();
        UserSecurityStampService service = new(CreateRedis(db));

        await service.BumpAsync(42, CancellationToken.None);

        await db.Received(1).StringSetAsync(
            (RedisKey)"user:stamp:42",
            Arg.Is<RedisValue>(v => v.HasValue && ((string?)v)!.Length > 0),
            Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }
}
