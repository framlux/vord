// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Server.Services.DataExport;

/// <summary>
/// Background service that deletes expired export files from object storage
/// and marks the corresponding jobs as expired.
/// </summary>
public sealed class DataExportCleanupService : BackgroundService
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(10);
    private const string LockKey = "lock:data-export-cleanup";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedLock _distributedLock;
    private readonly IObjectStorageService _objectStorageService;
    private readonly ILogger<DataExportCleanupService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="DataExportCleanupService"/> class.
    /// </summary>
    public DataExportCleanupService(
        IServiceScopeFactory scopeFactory,
        IDistributedLock distributedLock,
        IObjectStorageService objectStorageService,
        ILogger<DataExportCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _distributedLock = distributedLock;
        _objectStorageService = objectStorageService;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await using LockHandle? lockHandle = await _distributedLock.TryAcquireAsync(LockKey, LockTtl);
                if (lockHandle is null)
                {
                    _logger.LogDebug("Data export cleanup: another instance holds the lock, skipping this cycle");
                }
                else
                {
                    await CleanupExpiredExportsAsync(stoppingToken);
                }

                await Task.Delay(RunInterval, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during data export cleanup");
            }
        }
    }

    private async Task CleanupExpiredExportsAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        List<DataExportJob> expiredJobs = await db.DataExportJobs
            .Where(j => j.Status == DataExportJobStatus.Complete && j.ExpiresAt < now)
            .ToListAsync(ct);

        if (expiredJobs.Count == 0)
        {

            return;
        }

        _logger.LogInformation("Found {Count} expired export jobs to clean up", expiredJobs.Count);

        foreach (DataExportJob job in expiredJobs)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (string.IsNullOrEmpty(job.ObjectKey) == false)
                {
                    await _objectStorageService.DeleteObjectAsync(job.ObjectKey, ct);
                }

                await db.DataExportJobs
                    .Where(j => j.Id == job.Id)
                    .Set(j => j.Status, DataExportJobStatus.Expired)
                    .UpdateAsync(ct);

                _logger.LogInformation(
                    "Cleaned up expired export job {JobId} for tenant {TenantId}",
                    job.Id, job.TenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to clean up expired export job {JobId} for tenant {TenantId}",
                    job.Id, job.TenantId);
            }
        }
    }
}
