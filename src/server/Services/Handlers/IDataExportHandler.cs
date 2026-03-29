// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles tenant data export operations.
/// </summary>
public interface IDataExportHandler
{
    /// <summary>
    /// Creates a pending data export job for the given tenant.
    /// Returns the job ID for status polling.
    /// </summary>
    /// <param name="tenantId">The tenant whose data to export.</param>
    /// <param name="requestedByUserId">The user who requested the export.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A service result containing the created job ID.</returns>
    Task<ServiceResult<int>> ExportTenantDataAsync(int? tenantId, int requestedByUserId, CancellationToken ct);

    /// <summary>
    /// Processes a pending export job: generates the SQLite file, uploads to S3, and updates the job record.
    /// </summary>
    /// <param name="jobId">The export job ID to process.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ProcessExportJobAsync(int jobId, CancellationToken ct);

    /// <summary>
    /// Returns the current status and download URL (if complete) for an export job.
    /// Enforces tenant isolation.
    /// </summary>
    /// <param name="jobId">The export job ID.</param>
    /// <param name="tenantId">The requesting tenant's ID for isolation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A service result containing the export job details.</returns>
    Task<ServiceResult<DataExportJob>> GetExportJobAsync(int jobId, int? tenantId, CancellationToken ct);

    /// <summary>
    /// Returns an export job by its download token. No tenant isolation — the token IS the authorization.
    /// </summary>
    /// <param name="token">The download token.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A service result containing the export job details.</returns>
    Task<ServiceResult<DataExportJob>> GetExportJobByTokenAsync(string token, CancellationToken ct);
}
