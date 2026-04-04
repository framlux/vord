// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Server.Services.Retention;

/// <summary>
/// Background service that permanently removes soft-deleted alert events, audit log entries,
/// remote commands, and expired data export jobs older than a grace period. Runs daily after
/// the retention service, deleting in chunks to avoid long-running transactions.
/// Uses a distributed lock to ensure only one replica runs at a time.
/// </summary>
public sealed class DataCleanupService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromHours(3);
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan LockTtl = TimeSpan.FromHours(2);
    private const int BatchSize = 10_000;
    private const string LockKey = "lock:data-cleanup";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServerConfigurationService _configService;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<DataCleanupService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="DataCleanupService"/> class.
    /// </summary>
    public DataCleanupService(
        IServiceScopeFactory scopeFactory,
        ServerConfigurationService configService,
        IDistributedLock distributedLock,
        ILogger<DataCleanupService> logger)
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
                    _logger.LogDebug("Data cleanup: another instance holds the lock, skipping this cycle");
                }
                else
                {
                    await CleanupSoftDeletedRowsAsync(stoppingToken);
                }

                await Task.Delay(RunInterval, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error cleaning up soft-deleted data");
            }
        }
    }

    private async Task CleanupSoftDeletedRowsAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        TimeSpan gracePeriod = await _configService.GetTelemetryCleanupGracePeriodAsync(ct);
        DateTimeOffset graceCutoff = DateTimeOffset.UtcNow.Subtract(gracePeriod);

        int alertsDeleted = await DeleteInBatchesAsync(
            db,
            (database, ids) => database.AlertEvents.Where(e => ids.Contains(e.Id)).DeleteAsync(ct),
            db.AlertEvents
                .Where(e => e.DeletedAt != null && e.DeletedAt < graceCutoff)
                .Select(e => e.Id),
            ct);

        int auditDeleted = await DeleteInBatchesAsync(
            db,
            (database, ids) => database.AuditLog.Where(e => ids.Contains(e.Id)).DeleteAsync(ct),
            db.AuditLog
                .Where(e => e.DeletedAt != null && e.DeletedAt < graceCutoff)
                .Select(e => e.Id),
            ct);

        int commandsDeleted = await DeleteInBatchesAsync(
            db,
            (database, ids) => database.RemoteCommands.Where(c => ids.Contains(c.Id)).DeleteAsync(ct),
            db.RemoteCommands
                .Where(c => c.DeletedAt != null && c.DeletedAt < graceCutoff)
                .Select(c => c.Id),
            ct);

        int exportsDeleted = await DeleteExpiredExportsAsync(db, graceCutoff, ct);

        if ((alertsDeleted > 0) || (auditDeleted > 0) || (commandsDeleted > 0) || (exportsDeleted > 0))
        {
            _logger.LogInformation(
                "Data cleanup complete: deleted {Alerts} alerts, {Audit} audit entries, {Commands} commands, {Exports} exports (grace: {Days} days)",
                alertsDeleted, auditDeleted, commandsDeleted, exportsDeleted, gracePeriod.Days);
        }
    }

    private static async Task<int> DeleteInBatchesAsync(
        DatabaseContext db,
        Func<DatabaseContext, List<long>, Task<int>> deleteFunc,
        IQueryable<long> idQuery,
        CancellationToken ct)
    {
        int totalDeleted = 0;

        while (ct.IsCancellationRequested == false)
        {
            List<long> idsToDelete = await idQuery
                .Take(BatchSize)
                .ToListAsync(ct);

            if (idsToDelete.Count == 0)
            {
                break;
            }

            int deleted = await deleteFunc(db, idsToDelete);
            totalDeleted += deleted;

            if (deleted < BatchSize)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        return totalDeleted;
    }

    private static async Task<int> DeleteExpiredExportsAsync(
        DatabaseContext db, DateTimeOffset graceCutoff, CancellationToken ct)
    {
        int totalDeleted = 0;

        while (ct.IsCancellationRequested == false)
        {
            List<int> idsToDelete = await db.DataExportJobs
                .Where(j => j.Status == DataExportJobStatus.Expired && j.ExpiresAt < graceCutoff)
                .Select(j => j.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (idsToDelete.Count == 0)
            {
                break;
            }

            int deleted = await db.DataExportJobs
                .Where(j => idsToDelete.Contains(j.Id))
                .DeleteAsync(ct);

            totalDeleted += deleted;

            if (deleted < BatchSize)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        return totalDeleted;
    }
}
