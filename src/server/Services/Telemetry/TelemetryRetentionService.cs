// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using LinqToDB.Async;
using LinqToDB;

namespace Framlux.FleetManagement.Server.Services.Telemetry;

/// <summary>
/// Background service that periodically deletes telemetry data based on per-tenant retention policies.
/// Uses a distributed lock to ensure only one replica runs at a time.
/// </summary>
public sealed class TelemetryRetentionService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan LockTtl = TimeSpan.FromHours(2);
    private const string LockKey = "lock:telemetry-retention";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<TelemetryRetentionService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="TelemetryRetentionService"/> class.
    /// </summary>
    public TelemetryRetentionService(
        IServiceScopeFactory scopeFactory,
        IDistributedLock distributedLock,
        ILogger<TelemetryRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _distributedLock = distributedLock;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken);

        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await using LockHandle? lockHandle = await _distributedLock.TryAcquireAsync(LockKey, LockTtl);
                if (lockHandle is null)
                {
                    _logger.LogDebug("Telemetry retention: another instance holds the lock, skipping this cycle");
                }
                else
                {
                    await PurgeOldTelemetryAsync(stoppingToken);
                }

                await Task.Delay(RunInterval, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error purging old telemetry data");
            }
        }
    }

    private async Task PurgeOldTelemetryAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        // Get per-tenant retention policies.
        List<TenantSubscription> subscriptions = await db.TenantSubscriptions
            .ToListAsync(ct);

        int totalSoftDeleted = 0;

        foreach (TenantSubscription subscription in subscriptions)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-subscription.RetentionDays);

            // Soft-delete via subquery — no need to load machine IDs into C#.
            int softDeleted = await db.MachineTelemetry
                .Where(t => db.Machines
                    .Where(m => m.TenantId == subscription.TenantId)
                    .Select(m => m.Id)
                    .Contains(t.MachineId) &&
                    t.ReceivedAt < cutoff &&
                    t.DeletedAt == null)
                .Set(t => t.DeletedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(ct);

            if (softDeleted > 0)
            {
                _logger.LogInformation(
                    "Telemetry retention: soft-deleted {Count} rows for tenant {TenantId} (retention: {Days} days)",
                    softDeleted, subscription.TenantId, subscription.RetentionDays);
            }

            totalSoftDeleted += softDeleted;
        }

        _logger.LogInformation("Telemetry retention complete: soft-deleted {Count} total rows", totalSoftDeleted);
    }
}
