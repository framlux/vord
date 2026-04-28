// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Alerts;

/// <summary>
/// Background service that dequeues webhook delivery jobs from Redis and processes them
/// outside the alert evaluation lock.
/// </summary>
public sealed class WebhookDeliveryWorkerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebhookDeliveryWorkerService> _logger;

    private const string DeliveryQueueKey = "alert:delivery:queue";
    private static readonly TimeSpan BrpopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Creates a new instance of the <see cref="WebhookDeliveryWorkerService"/> class.
    /// </summary>
    public WebhookDeliveryWorkerService(
        IConnectionMultiplexer redis,
        IAlertDeliveryService deliveryService,
        IServiceScopeFactory scopeFactory,
        ILogger<WebhookDeliveryWorkerService> logger)
    {
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
                _logger.LogError(ex, "Unexpected error in webhook delivery worker");
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
        RedisValue result = await redisDb.ListRightPopAsync(DeliveryQueueKey);

        if (result.IsNullOrEmpty)
        {
            // No job available; wait before polling again
            await Task.Delay(BrpopTimeout, ct);

            return;
        }

        await ProcessJobAsync(result.ToString(), ct);
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
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        AlertEvent? alertEvent = await db.AlertEvents
            .FirstOrDefaultAsync(e => e.Id == job.EventId, ct);

        if (alertEvent is null)
        {
            _logger.LogError("Alert event {EventId} not found for delivery job", job.EventId);

            return;
        }

        AlertRule? rule = await db.AlertRules
            .FirstOrDefaultAsync(r => r.Id == job.RuleId, ct);

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
            _logger.LogError(ex, "Failed to deliver alert event {EventId} for rule {RuleId}", job.EventId, job.RuleId);
        }
    }

    /// <summary>
    /// Represents a serialized delivery job from the Redis queue.
    /// </summary>
    internal sealed class DeliveryJob
    {
        /// <summary>The alert event identifier.</summary>
        public long EventId { get; set; }

        /// <summary>The alert rule identifier.</summary>
        public int RuleId { get; set; }

        /// <summary>The tenant identifier.</summary>
        public int TenantId { get; set; }
    }
}
