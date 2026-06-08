// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Hangfire;

namespace Framlux.FleetManagement.Services.Core.DataExport;

/// <summary>
/// Hangfire recurring job that deletes expired tenant data export files from object storage and
/// marks the corresponding job rows as <see cref="Framlux.FleetManagement.Database.Enums.DataExportJobStatus.Expired"/>.
/// Replaces the former DataExportCleanupService.
/// </summary>
public sealed class DataExportCleanupJob
{
    private readonly IDataExportRepository _dataExportRepository;
    private readonly IObjectStorageService _objectStorageService;
    private readonly ILogger<DataExportCleanupJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="DataExportCleanupJob"/> class.
    /// </summary>
    /// <param name="dataExportRepository">Source of expired export-job rows.</param>
    /// <param name="objectStorageService">Object storage used to delete the export blobs.</param>
    /// <param name="logger">The logger.</param>
    public DataExportCleanupJob(
        IDataExportRepository dataExportRepository,
        IObjectStorageService objectStorageService,
        ILogger<DataExportCleanupJob> logger)
    {
        ArgumentNullException.ThrowIfNull(dataExportRepository);
        ArgumentNullException.ThrowIfNull(objectStorageService);
        ArgumentNullException.ThrowIfNull(logger);

        _dataExportRepository = dataExportRepository;
        _objectStorageService = objectStorageService;
        _logger = logger;
    }

    /// <summary>
    /// Marks every expired export job as <c>Expired</c> in the database and removes the corresponding
    /// blobs from object storage. Per-job failures are swallowed and logged so a single bad row
    /// does not halt the cleanup pass; top-level listing failures propagate so Hangfire records the
    /// run as failed.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("long")]
    public async Task RunAsync(CancellationToken ct)
    {
        List<DataExportJob> expiredJobs = await _dataExportRepository.GetExpiredExportJobsAsync(ct);

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
                await _dataExportRepository.ExpireExportJobAsync(job.Id, ct);

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
