// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.DataExport;

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
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(distributedLock);
        ArgumentNullException.ThrowIfNull(objectStorageService);
        ArgumentNullException.ThrowIfNull(logger);

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

    internal async Task CleanupExpiredExportsAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IDataExportRepository exportRepo = scope.ServiceProvider.GetRequiredService<IDataExportRepository>();

        List<DataExportJob> expiredJobs = await exportRepo.GetExpiredExportJobsAsync(ct);

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
                // Mark as expired in the database first — this is the authoritative state.
                // If the S3 delete fails afterward, the object is orphaned but the user
                // cannot re-download it (safe direction). The reverse order risked deleting
                // the S3 object but leaving the DB record active if the DB update failed.
                await exportRepo.ExpireExportJobAsync(job.Id, ct);

                if (string.IsNullOrEmpty(job.ObjectKey) == false)
                {
                    try
                    {
                        await _objectStorageService.DeleteObjectAsync(job.ObjectKey, ct);
                    }
                    catch (Exception storageEx)
                    {
                        _logger.LogWarning(storageEx,
                            "Failed to delete S3 object {ObjectKey} for expired export job {JobId} — object may be orphaned",
                            job.ObjectKey, job.Id);
                    }
                }

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
