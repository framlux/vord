// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="ServerConfigurationService"/>.
/// </summary>
public class ServerConfigurationServiceTests
{
    private static (ServerConfigurationService service, IServerSettingsCache cache, IDatabase redisDb) CreateService()
    {
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        ServerConfigurationService service = new(cache, redis);

        return (service, cache, redisDb);
    }

    // ========== GetAgentHeartbeatSecondsAsync tests ==========

    [Test]
    public async Task GetAgentHeartbeatSecondsAsync_RedisCacheHit_ReturnsCachedValue()
    {
        (ServerConfigurationService service, IServerSettingsCache cache, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>("600"));

        int result = await service.GetAgentHeartbeatSecondsAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(600);
        await cache.DidNotReceive().GetSettingAsync(Arg.Any<ServerConfigurationSettingKeys>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetAgentHeartbeatSecondsAsync_RedisMiss_DbHit_ReturnsDbValue()
    {
        (ServerConfigurationService service, IServerSettingsCache cache, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));
        cache.GetSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, Arg.Any<CancellationToken>())
            .Returns("900");

        int result = await service.GetAgentHeartbeatSecondsAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(900);
    }

    [Test]
    public async Task GetAgentHeartbeatSecondsAsync_RedisMiss_DbHit_CachesInRedis()
    {
        (ServerConfigurationService service, IServerSettingsCache cache, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));
        cache.GetSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, Arg.Any<CancellationToken>())
            .Returns("900");

        await service.GetAgentHeartbeatSecondsAsync(CancellationToken.None);

        await redisDb.Received(1).StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Is<RedisValue>(v => v == "900"),
            Arg.Is<TimeSpan>(t => t == TimeSpan.FromMinutes(5)),
            Arg.Any<bool>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task GetAgentHeartbeatSecondsAsync_RedisMiss_DbMiss_ReturnsDefault300()
    {
        (ServerConfigurationService service, IServerSettingsCache cache, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));
        cache.GetSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        int result = await service.GetAgentHeartbeatSecondsAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(300);
    }

    [Test]
    public async Task GetAgentHeartbeatSecondsAsync_RedisInvalidValue_FallsBackToDb()
    {
        (ServerConfigurationService service, IServerSettingsCache cache, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>("not-a-number"));
        cache.GetSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, Arg.Any<CancellationToken>())
            .Returns("450");

        int result = await service.GetAgentHeartbeatSecondsAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(450);
    }

    [Test]
    public async Task GetAgentHeartbeatSecondsAsync_RedisNegativeValue_FallsBackToDb()
    {
        (ServerConfigurationService service, IServerSettingsCache cache, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>("-5"));
        cache.GetSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        int result = await service.GetAgentHeartbeatSecondsAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(300);
    }

    [Test]
    public async Task GetAgentHeartbeatSecondsAsync_DbInvalidValue_ReturnsDefault()
    {
        (ServerConfigurationService service, IServerSettingsCache cache, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));
        cache.GetSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, Arg.Any<CancellationToken>())
            .Returns("invalid");

        int result = await service.GetAgentHeartbeatSecondsAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(300);
    }

    [Test]
    public async Task GetAgentHeartbeatSecondsAsync_RedisZeroValue_FallsBackToDb()
    {
        (ServerConfigurationService service, IServerSettingsCache cache, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>("0"));
        cache.GetSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, Arg.Any<CancellationToken>())
            .Returns("120");

        int result = await service.GetAgentHeartbeatSecondsAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(120);
    }

    // ========== GetAgentConfigRefreshSecondsAsync tests ==========

    [Test]
    public async Task GetAgentConfigRefreshSecondsAsync_Default_Returns900()
    {
        (ServerConfigurationService service, IServerSettingsCache _, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        int result = await service.GetAgentConfigRefreshSecondsAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(900);
    }

    // ========== GetOnlineThresholdAsync tests ==========

    [Test]
    public async Task GetOnlineThresholdAsync_Default_Returns300Seconds()
    {
        (ServerConfigurationService service, IServerSettingsCache _, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        TimeSpan result = await service.GetOnlineThresholdAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(TimeSpan.FromSeconds(300));
    }

    [Test]
    public async Task GetOnlineThresholdAsync_CustomValue_ReturnsAsTimeSpan()
    {
        (ServerConfigurationService service, IServerSettingsCache _, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>("600"));

        TimeSpan result = await service.GetOnlineThresholdAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(TimeSpan.FromSeconds(600));
    }

    // ========== GetCertificateExpiryWarningDaysAsync tests ==========

    [Test]
    public async Task GetCertificateExpiryWarningDaysAsync_Default_Returns30()
    {
        (ServerConfigurationService service, IServerSettingsCache _, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        int result = await service.GetCertificateExpiryWarningDaysAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(30);
    }

    // ========== GetDeduplicationTtlAsync tests ==========

    [Test]
    public async Task GetDeduplicationTtlAsync_Default_Returns300Seconds()
    {
        (ServerConfigurationService service, IServerSettingsCache _, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        TimeSpan result = await service.GetDeduplicationTtlAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(TimeSpan.FromSeconds(300));
    }
}
