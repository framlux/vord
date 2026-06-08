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
    public async Task<bool> TryClaimPendingJobAsync(int jobId, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        // Conditional UPDATE: only the row whose status is still Pending flips to Processing,
        // and the rows-affected count tells us whether THIS caller is the one that claimed it.
        // If another worker already claimed (or the row was deleted), the count is 0 and we exit.
        int affected = await _db.DataExportJobs
            .Where(j => (j.Id == jobId) && (j.Status == DataExportJobStatus.Pending))
            .Set(j => j.Status, DataExportJobStatus.Processing)
            .Set(j => j.StartedAt, (DateTimeOffset?)startedAt)
            .UpdateAsync(cancellationToken);

        return affected == 1;
    }

    /// <inheritdoc/>
    public async Task<List<DataExportJob>> GetStuckProcessingJobsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        List<DataExportJob> jobs = await _db.DataExportJobs
            .Where(j => (j.Status == DataExportJobStatus.Processing)
                     && (j.StartedAt != null)
                     && (j.StartedAt < olderThan))
            .ToListAsync(cancellationToken);

        return jobs;
    }

    /// <inheritdoc/>
    public async Task ResetOrphanedJobToPendingAsync(int jobId, CancellationToken cancellationToken)
    {
        // Only reset if it is still in Processing. A concurrent completion would have moved it
        // to Complete already; we must not clobber that.
        await _db.DataExportJobs
            .Where(j => (j.Id == jobId) && (j.Status == DataExportJobStatus.Processing))
            .Set(j => j.Status, DataExportJobStatus.Pending)
            .Set(j => j.StartedAt, (DateTimeOffset?)null)
            .UpdateAsync(cancellationToken);

        _logger.LogWarning("Reset orphaned export job {JobId} from Processing back to Pending", jobId);
    }

    /// <inheritdoc/>
    public async Task<int> IncrementFailureCountAsync(int jobId, CancellationToken cancellationToken)
    {
        await _db.DataExportJobs
            .Where(j => j.Id == jobId)
            .Set(j => j.FailureCount, j => j.FailureCount + 1)
            .UpdateAsync(cancellationToken);

        int newCount = await _db.DataExportJobs
            .Where(j => j.Id == jobId)
            .Select(j => j.FailureCount)
            .FirstOrDefaultAsync(cancellationToken);

        return newCount;
    }

    /// <inheritdoc/>
    public async Task MarkExportJobFailedAsync(int jobId, string errorMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(errorMessage);
        await _db.DataExportJobs
            .Where(j => j.Id == jobId)
            .Set(j => j.Status, DataExportJobStatus.Failed)
            .Set(j => j.ErrorMessage, errorMessage)
            .Set(j => j.CompletedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);

        _logger.LogWarning("Marked export job {JobId} as Failed after exhausting retries", jobId);
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
