// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IDataExportRepository
{
    /// <inheritdoc/>
    public async Task<DataExportJob> CreateExportJobAsync(DataExportJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        job.Id = await _db.InsertWithInt32IdentityAsync(job, token: cancellationToken);

        _logger.LogDebug("Created data export job {JobId} for tenant {TenantId}", job.Id, job.TenantId);

        return job;
    }

    /// <inheritdoc/>
    public async Task<DataExportJob?> GetExportJobByIdAsync(int jobId, CancellationToken cancellationToken)
    {
        DataExportJob? job = await _db.DataExportJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        return job;
    }

    /// <inheritdoc/>
    public async Task<DataExportJob?> GetExportJobByTokenAsync(string downloadToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadToken);

        DataExportJob? job = await _db.DataExportJobs
            .FirstOrDefaultAsync(j => j.DownloadToken == downloadToken, cancellationToken);

        return job;
    }

    /// <inheritdoc/>
    public async Task<bool> HasActiveExportJobAsync(int tenantId, CancellationToken cancellationToken)
    {
        bool hasActive = await _db.DataExportJobs
            .AnyAsync(j => (j.TenantId == tenantId) &&
                           ((j.Status == DataExportJobStatus.Pending) || (j.Status == DataExportJobStatus.Processing)),
                      cancellationToken);

        return hasActive;
    }

    /// <inheritdoc/>
    public async Task UpdateExportJobStatusAsync(int jobId, DataExportJobStatus status, CancellationToken cancellationToken)
    {
        await _db.DataExportJobs
            .Where(j => j.Id == jobId)
            .Set(j => j.Status, status)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task CompleteExportJobAsync(int jobId, string objectKey, long fileSizeBytes, CancellationToken cancellationToken)
    {
        await _db.DataExportJobs
            .Where(j => j.Id == jobId)
            .Set(j => j.Status, DataExportJobStatus.Complete)
            .Set(j => j.CompletedAt, DateTimeOffset.UtcNow)
            .Set(j => j.ObjectKey, objectKey)
            .Set(j => j.FileSizeBytes, fileSizeBytes)
            .UpdateAsync(cancellationToken);

        _logger.LogDebug("Marked export job {JobId} as complete", jobId);
    }

    /// <inheritdoc/>
    public async Task FailExportJobAsync(int jobId, string errorMessage, CancellationToken cancellationToken)
    {
        await _db.DataExportJobs
            .Where(j => j.Id == jobId)
            .Set(j => j.Status, DataExportJobStatus.Failed)
            .Set(j => j.ErrorMessage, errorMessage)
            .UpdateAsync(cancellationToken);

        _logger.LogWarning("Marked export job {JobId} as failed: {Error}", jobId, errorMessage);
    }

    /// <inheritdoc/>
    public async Task<List<DataExportJob>> GetPendingExportJobsAsync(CancellationToken cancellationToken)
    {
        List<DataExportJob> jobs = await _db.DataExportJobs
            .Where(j => j.Status == DataExportJobStatus.Pending)
            .OrderBy(j => j.RequestedAt)
            .ToListAsync(cancellationToken);

        return jobs;
    }

    /// <inheritdoc/>
    public async Task<List<DataExportJob>> GetExpiredExportJobsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        List<DataExportJob> jobs = await _db.DataExportJobs
            .Where(j => (j.Status == DataExportJobStatus.Complete) && (j.ExpiresAt < now))
            .ToListAsync(cancellationToken);

        return jobs;
    }

    /// <inheritdoc/>
    public async Task ExpireExportJobAsync(int jobId, CancellationToken cancellationToken)
    {
        await _db.DataExportJobs
            .Where(j => j.Id == jobId)
            .Set(j => j.Status, DataExportJobStatus.Expired)
            .UpdateAsync(cancellationToken);

        _logger.LogDebug("Marked export job {JobId} as expired", jobId);
    }
}
