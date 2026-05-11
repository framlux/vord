// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Alerts;

/// <summary>
/// Background service that dequeues integration delivery jobs from Redis and processes them
/// outside the alert evaluation lock.
/// </summary>
public sealed class IntegrationDeliveryWorkerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IntegrationDeliveryWorkerService> _logger;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Creates a new instance of the <see cref="IntegrationDeliveryWorkerService"/> class.
    /// </summary>
    public IntegrationDeliveryWorkerService(
        IConnectionMultiplexer redis,
        IAlertDeliveryService deliveryService,
        IServiceScopeFactory scopeFactory,
        ILogger<IntegrationDeliveryWorkerService> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(deliveryService);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _redis = redis;
        _deliveryService = deliveryService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await ProcessNextJobAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in integration delivery worker");
            }
        }
    }

    /// <summary>
    /// Attempts to dequeue and process a single delivery job from Redis.
    /// Exposed as internal for unit testing.
    /// </summary>
    internal async Task ProcessNextJobAsync(CancellationToken ct)
    {
        IDatabase redisDb = _redis.GetDatabase();
        RedisValue result = await redisDb.ListRightPopAsync(AlertConstants.DeliveryQueueKey);

        if (result.IsNullOrEmpty)
        {
            await Task.Delay(PollingInterval, ct);

            return;
        }

        string payload = result.ToString();

        DeliveryJob? peekedJob = null;
        try
        {
            peekedJob = JsonSerializer.Deserialize<DeliveryJob>(payload, JsonDefaults.CamelCase);
        }
        catch
        {
            // Deserialization errors are handled in ProcessJobAsync
        }

        if ((peekedJob?.NotBefore is not null) && (peekedJob.NotBefore.Value > DateTimeOffset.UtcNow))
        {
            await redisDb.ListRightPushAsync(AlertConstants.DeliveryQueueKey, payload);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);

            return;
        }

        await ProcessJobAsync(payload, ct);
    }

    /// <summary>
    /// Processes a single delivery job payload.
    /// Exposed as internal for unit testing.
    /// </summary>
    internal async Task ProcessJobAsync(string? payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            _logger.LogError("Received null or empty delivery job payload");

            return;
        }

        DeliveryJob? job;
        try
        {
            job = JsonSerializer.Deserialize<DeliveryJob>(payload, JsonDefaults.CamelCase);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize delivery job payload: {Payload}", payload);

            return;
        }

        if (job is null)
        {
            _logger.LogError("Deserialized delivery job was null for payload: {Payload}", payload);

            return;
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        IAlertEventRepository alertEventRepo = scope.ServiceProvider.GetRequiredService<IAlertEventRepository>();
        IAlertRuleRepository alertRuleRepo = scope.ServiceProvider.GetRequiredService<IAlertRuleRepository>();

        AlertEvent? alertEvent = await alertEventRepo.GetAlertEventByIdAsync(job.EventId, ct);

        if (alertEvent is null)
        {
            _logger.LogError("Alert event {EventId} not found for delivery job", job.EventId);

            return;
        }

        AlertRule? rule = await alertRuleRepo.GetAlertRuleByIdAsync(job.RuleId, ct);

        if (rule is null)
        {
            _logger.LogError("Alert rule {RuleId} not found for delivery job", job.RuleId);

            return;
        }

        try
        {
            await _deliveryService.DeliverAsync(alertEvent, rule, ct);
        }
        catch (Exception ex)
        {
            job.RetryCount++;
            if (job.RetryCount < 3)
            {
                TimeSpan retryDelay = TimeSpan.FromSeconds(10 * Math.Pow(2, job.RetryCount - 1));
                job.NotBefore = DateTimeOffset.UtcNow.Add(retryDelay);
                _logger.LogWarning(ex, "Delivery attempt {Attempt} failed for event {EventId}, scheduling retry at {NotBefore}",
                    job.RetryCount, job.EventId, job.NotBefore.Value.ToString("o"));
                string retryPayload = JsonSerializer.Serialize(job, JsonDefaults.CamelCase);
                IDatabase retryDb = _redis.GetDatabase();
                await retryDb.ListLeftPushAsync(AlertConstants.DeliveryQueueKey, retryPayload);
            }
            else
            {
                _logger.LogError(ex, "Delivery exhausted all retries for event {EventId}, rule {RuleId}. Moving to dead letter queue",
                    job.EventId, job.RuleId);
                string deadLetterPayload = JsonSerializer.Serialize(job, JsonDefaults.CamelCase);
                IDatabase dlDb = _redis.GetDatabase();
                await dlDb.ListLeftPushAsync(AlertConstants.DeliveryDeadLetterKey, deadLetterPayload);
            }
        }
    }
}
