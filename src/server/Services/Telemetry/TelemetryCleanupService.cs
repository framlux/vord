// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using LinqToDB.Async;
using LinqToDB;

namespace Framlux.FleetManagement.Server.Services.Telemetry;

/// <summary>
/// Background service that permanently removes soft-deleted telemetry rows older than a grace period.
/// Runs daily after the retention service, deleting in chunks to avoid long-running transactions.
/// Uses a distributed lock to ensure only one replica runs at a time.
/// </summary>
public sealed class TelemetryCleanupService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromHours(2);
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan LockTtl = TimeSpan.FromHours(2);
    private const int BatchSize = 10_000;
    private const string LockKey = "lock:telemetry-cleanup";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServerConfigurationService _configService;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<TelemetryCleanupService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="TelemetryCleanupService"/> class.
    /// </summary>
    public TelemetryCleanupService(
        IServiceScopeFactory scopeFactory,
        ServerConfigurationService configService,
        IDistributedLock distributedLock,
        ILogger<TelemetryCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _configService = configService;
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
                    _logger.LogDebug("Telemetry cleanup: another instance holds the lock, skipping this cycle");
                }
                else
                {
                    await CleanupSoftDeletedRowsAsync(stoppingToken);
                }

                await Task.Delay(RunInterval, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error cleaning up soft-deleted telemetry data");
            }
        }
    }

    private async Task CleanupSoftDeletedRowsAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        TimeSpan gracePeriod = await _configService.GetTelemetryCleanupGracePeriodAsync(ct);
        DateTimeOffset graceCutoff = DateTimeOffset.UtcNow.Subtract(gracePeriod);
        int totalDeleted = 0;

        // Delete in chunks. We select IDs first, then delete by IDs, because
        // LinqToDB's .Take().DeleteAsync() generates DELETE ... LIMIT which
        // is not supported by all databases (e.g. SQLite).
        while (ct.IsCancellationRequested == false)
        {
            List<long> idsToDelete = await db.MachineTelemetry
                .Where(t => t.DeletedAt != null && t.DeletedAt < graceCutoff)
                .Select(t => t.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (idsToDelete.Count == 0)
            {
                break;
            }

            int deleted = await db.MachineTelemetry
                .Where(t => idsToDelete.Contains(t.Id))
                .DeleteAsync(ct);

            totalDeleted += deleted;

            if (deleted < BatchSize)
            {
                break;
            }

            // Small delay between chunks to reduce DB pressure.
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        if (totalDeleted > 0)
        {
            _logger.LogInformation(
                "Telemetry cleanup: permanently deleted {Count} soft-deleted rows older than {GraceDays} days",
                totalDeleted, gracePeriod.Days);
        }
    }
}
