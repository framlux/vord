// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Alerts;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Alerts;

/// <summary>
/// Tests for <see cref="IntegrationDeliveryWorkerService"/>.
/// </summary>
public sealed class IntegrationDeliveryWorkerServiceTests
{
    private static AlertEvent CreateAlertEvent(int id = 100, int ruleId = 1, int tenantId = 1)
    {
        return new AlertEvent
        {
            Id = id,
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 42,
            Severity = AlertSeverity.Critical,
            Message = "CPU at 95%",
            Details = """{"metric":"CpuUsage","currentValue":95}""",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
    }

    private static AlertRule CreateAlertRule(int id = 1, int tenantId = 1)
    {
        return new AlertRule
        {
            Id = id,
            TenantId = tenantId,
            Name = "Test Rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            NotifyWebhook = true,
            IsCustom = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static (IntegrationDeliveryWorkerService service, IAlertDeliveryService deliveryService, IConnectionMultiplexer redis, ILogger<IntegrationDeliveryWorkerService> logger) BuildService(
        IAlertEventRepository? alertEventRepo = null,
        IAlertRuleRepository? alertRuleRepo = null)
    {
        IAlertDeliveryService deliveryService = Substitute.For<IAlertDeliveryService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        ILogger<IntegrationDeliveryWorkerService> logger = Substitute.For<ILogger<IntegrationDeliveryWorkerService>>();

        IAlertEventRepository eventRepo = alertEventRepo ?? Substitute.For<IAlertEventRepository>();
        IAlertRuleRepository ruleRepo = alertRuleRepo ?? Substitute.For<IAlertRuleRepository>();

        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IAlertEventRepository)).Returns(eventRepo);
        serviceProvider.GetService(typeof(IAlertRuleRepository)).Returns(ruleRepo);
        scope.ServiceProvider.Returns(serviceProvider);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        IntegrationDeliveryWorkerService service = new(redis, deliveryService, scopeFactory, logger);

        return (service, deliveryService, redis, logger);
    }

    // --- ProcessJobAsync Tests ---

    [Test]
    public async Task ProcessJobAsync_NullPayload_LogsErrorAndReturns()
    {
        (IntegrationDeliveryWorkerService service, IAlertDeliveryService deliveryService, _, ILogger<IntegrationDeliveryWorkerService> logger) = BuildService();

        await service.ProcessJobAsync(null, CancellationToken.None);

        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("null or empty")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        await deliveryService.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJobAsync_InvalidJson_LogsErrorAndReturns()
    {
        (IntegrationDeliveryWorkerService service, IAlertDeliveryService deliveryService, _, ILogger<IntegrationDeliveryWorkerService> logger) = BuildService();

        await service.ProcessJobAsync("not-valid-json{{{", CancellationToken.None);

        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("deserialize")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        await deliveryService.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJobAsync_NullDeserializedJob_LogsErrorAndReturns()
    {
        (IntegrationDeliveryWorkerService service, IAlertDeliveryService deliveryService, _, ILogger<IntegrationDeliveryWorkerService> logger) = BuildService();

        // JSON "null" deserializes to null
        await service.ProcessJobAsync("null", CancellationToken.None);

        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("null")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        await deliveryService.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJobAsync_MissingAlertEvent_LogsErrorAndReturns()
    {
        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        eventRepo.GetAlertEventByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns((AlertEvent?)null);

        (IntegrationDeliveryWorkerService service, IAlertDeliveryService deliveryService, _, ILogger<IntegrationDeliveryWorkerService> logger) = BuildService(alertEventRepo: eventRepo);

        string payload = JsonSerializer.Serialize(new { eventId = 999, ruleId = 1, tenantId = 1, retryCount = 0 }, JsonDefaults.CamelCase);
        await service.ProcessJobAsync(payload, CancellationToken.None);

        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("not found")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        await deliveryService.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJobAsync_MissingAlertRule_LogsErrorAndReturns()
    {
        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        eventRepo.GetAlertEventByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(CreateAlertEvent());

        IAlertRuleRepository ruleRepo = Substitute.For<IAlertRuleRepository>();
        ruleRepo.GetAlertRuleByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((AlertRule?)null);

        (IntegrationDeliveryWorkerService service, IAlertDeliveryService deliveryService, _, ILogger<IntegrationDeliveryWorkerService> logger) = BuildService(alertEventRepo: eventRepo, alertRuleRepo: ruleRepo);

        string payload = JsonSerializer.Serialize(new { eventId = 100, ruleId = 999, tenantId = 1, retryCount = 0 }, JsonDefaults.CamelCase);
        await service.ProcessJobAsync(payload, CancellationToken.None);

        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("rule") || o.ToString()!.Contains("Rule")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
        await deliveryService.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJobAsync_Success_CallsDeliverAsync()
    {
        AlertEvent alertEvent = CreateAlertEvent();
        AlertRule alertRule = CreateAlertRule();

        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        eventRepo.GetAlertEventByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(alertEvent);

        IAlertRuleRepository ruleRepo = Substitute.For<IAlertRuleRepository>();
        ruleRepo.GetAlertRuleByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(alertRule);

        (IntegrationDeliveryWorkerService service, IAlertDeliveryService deliveryService, _, _) = BuildService(alertEventRepo: eventRepo, alertRuleRepo: ruleRepo);

        string payload = JsonSerializer.Serialize(new { eventId = 100, ruleId = 1, tenantId = 1, retryCount = 0 }, JsonDefaults.CamelCase);
        await service.ProcessJobAsync(payload, CancellationToken.None);

        await deliveryService.Received(1).DeliverAsync(alertEvent, alertRule, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJobAsync_DeliveryFailure_IncrementsRetryCountAndRequeues()
    {
        AlertEvent alertEvent = CreateAlertEvent();
        AlertRule alertRule = CreateAlertRule();

        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        eventRepo.GetAlertEventByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(alertEvent);

        IAlertRuleRepository ruleRepo = Substitute.For<IAlertRuleRepository>();
        ruleRepo.GetAlertRuleByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(alertRule);

        (IntegrationDeliveryWorkerService service, IAlertDeliveryService deliveryService, IConnectionMultiplexer redis, ILogger<IntegrationDeliveryWorkerService> logger) = BuildService(alertEventRepo: eventRepo, alertRuleRepo: ruleRepo);

        deliveryService.DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Delivery failed"));

        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        // retryCount=0 means first failure will increment to 1, which is < 3, so it retries
        string payload = JsonSerializer.Serialize(new { eventId = 100, ruleId = 1, tenantId = 1, retryCount = 0 }, JsonDefaults.CamelCase);
        await service.ProcessJobAsync(payload, CancellationToken.None);

        // Should push to the main queue for retry, not the dead letter queue
        await redisDb.Received(1).ListLeftPushAsync(
            Arg.Is<RedisKey>(k => k == "alert:delivery:queue"),
            Arg.Any<RedisValue>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());

        // Verify the re-queued payload has incremented retryCount
        await redisDb.Received(1).ListLeftPushAsync(
            Arg.Any<RedisKey>(),
            Arg.Is<RedisValue>(v => v.ToString().Contains("\"retryCount\":1")),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task ProcessJobAsync_ExhaustedRetries_MovesToDeadLetterQueue()
    {
        AlertEvent alertEvent = CreateAlertEvent();
        AlertRule alertRule = CreateAlertRule();

        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        eventRepo.GetAlertEventByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(alertEvent);

        IAlertRuleRepository ruleRepo = Substitute.For<IAlertRuleRepository>();
        ruleRepo.GetAlertRuleByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(alertRule);

        (IntegrationDeliveryWorkerService service, IAlertDeliveryService deliveryService, IConnectionMultiplexer redis, ILogger<IntegrationDeliveryWorkerService> logger) = BuildService(alertEventRepo: eventRepo, alertRuleRepo: ruleRepo);

        deliveryService.DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Delivery failed"));

        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        // retryCount=2 means after increment it becomes 3, which is >= 3, so dead letter
        string payload = JsonSerializer.Serialize(new { eventId = 100, ruleId = 1, tenantId = 1, retryCount = 2 }, JsonDefaults.CamelCase);
        await service.ProcessJobAsync(payload, CancellationToken.None);

        // Should push to the dead letter queue
        await redisDb.Received(1).ListLeftPushAsync(
            Arg.Is<RedisKey>(k => k == "alert:delivery:deadletter"),
            Arg.Any<RedisValue>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());

        // Should NOT push to the main queue
        await redisDb.DidNotReceive().ListLeftPushAsync(
            Arg.Is<RedisKey>(k => k == "alert:delivery:queue"),
            Arg.Any<RedisValue>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());
    }

    // --- ProcessNextJobAsync Tests ---

    [Test]
    public async Task ProcessNextJobAsync_EmptyQueue_DelaysWithoutProcessing()
    {
        (IntegrationDeliveryWorkerService service, IAlertDeliveryService deliveryService, IConnectionMultiplexer redis, _) = BuildService();

        IDatabase redisDb = Substitute.For<IDatabase>();
        redisDb.ListRightPopAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        using CancellationTokenSource cts = new();

        // ProcessNextJobAsync will call Task.Delay, which we can test by verifying
        // no delivery was attempted
        await service.ProcessNextJobAsync(cts.Token);

        await deliveryService.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessNextJobAsync_NotBeforeInFuture_RequeuesJob()
    {
        (IntegrationDeliveryWorkerService service, IAlertDeliveryService deliveryService, IConnectionMultiplexer redis, _) = BuildService();

        // Create a job with NotBefore set far in the future
        DateTimeOffset futureTime = DateTimeOffset.UtcNow.AddHours(1);
        string jobPayload = JsonSerializer.Serialize(new
        {
            eventId = 100,
            ruleId = 1,
            tenantId = 1,
            retryCount = 0,
            notBefore = futureTime,
        }, JsonDefaults.CamelCase);

        IDatabase redisDb = Substitute.For<IDatabase>();
        redisDb.ListRightPopAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)jobPayload);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        using CancellationTokenSource cts = new();

        await service.ProcessNextJobAsync(cts.Token);

        // Should re-queue the job back to the delivery queue
        await redisDb.Received(1).ListRightPushAsync(
            Arg.Is<RedisKey>(k => k == "alert:delivery:queue"),
            Arg.Is<RedisValue>(v => v.ToString() == jobPayload),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());

        // Should NOT attempt delivery
        await deliveryService.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }
}
