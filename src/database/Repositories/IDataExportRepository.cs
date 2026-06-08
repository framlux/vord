// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for data export job operations.
/// </summary>
public interface IDataExportRepository
{
    /// <summary>
    /// Inserts a new data export job and sets its generated ID.
    /// </summary>
    Task<DataExportJob> CreateExportJobAsync(DataExportJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a data export job by ID, or null if not found.
    /// </summary>
    Task<DataExportJob?> GetExportJobByIdAsync(int jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a data export job matching the download token, or null if not found.
    /// </summary>
    Task<DataExportJob?> GetExportJobByTokenAsync(string downloadToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if the tenant has a Pending or Processing export job.
    /// </summary>
    Task<bool> HasActiveExportJobAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the status of an export job.
    /// </summary>
    Task UpdateExportJobStatusAsync(int jobId, DataExportJobStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an export job as complete with storage details.
    /// </summary>
    Task CompleteExportJobAsync(int jobId, string objectKey, long fileSizeBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an export job as failed with an error message.
    /// </summary>
    Task FailExportJobAsync(int jobId, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all pending export jobs ordered by request time.
    /// </summary>
    Task<List<DataExportJob>> GetPendingExportJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically transitions a job from Pending to Processing, stamping <paramref name="startedAt"/>.
    /// Returns <c>true</c> when this caller is the one that claimed the job (exactly one row was
    /// updated); <c>false</c> if the job was no longer Pending. Used by DataExportProcessingJob to
    /// prevent two workers from processing the same row.
    /// </summary>
    Task<bool> TryClaimPendingJobAsync(int jobId, DateTimeOffset startedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns export jobs stuck in Processing for longer than <paramref name="olderThan"/> — these
    /// are orphans from a worker that crashed mid-export. The processing job re-claims them by
    /// resetting Status to Pending so a fresh worker can pick them up.
    /// </summary>
    Task<List<DataExportJob>> GetStuckProcessingJobsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets an orphaned Processing job back to Pending so the next processing tick can claim
    /// and re-run it. Idempotent — if the row is not in Processing status, this is a no-op.
    /// </summary>
    Task ResetOrphanedJobToPendingAsync(int jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments <c>FailureCount</c> for the supplied job and returns the resulting
    /// value. Used by <c>DataExportProcessingJob</c> to detect when a job has exhausted its
    /// retry budget so it can be transitioned to Failed instead of being re-claimed forever.
    /// </summary>
    Task<int> IncrementFailureCountAsync(int jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions a job to <see cref="Enums.DataExportJobStatus.Failed"/> with the supplied
    /// error message and the current UTC timestamp. Used when the failure count exceeds the
    /// retry budget — the row remains in storage for diagnostics but no longer cycles.
    /// </summary>
    Task MarkExportJobFailedAsync(int jobId, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all completed export jobs that have passed their expiration time.
    /// </summary>
    Task<List<DataExportJob>> GetExpiredExportJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an export job as expired.
    /// </summary>
    Task ExpireExportJobAsync(int jobId, CancellationToken cancellationToken = default);
}
