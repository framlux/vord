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
    /// Returns all completed export jobs that have passed their expiration time.
    /// </summary>
    Task<List<DataExportJob>> GetExpiredExportJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an export job as expired.
    /// </summary>
    Task ExpireExportJobAsync(int jobId, CancellationToken cancellationToken = default);
}
