// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Request model for the data export status endpoint.
/// </summary>
public sealed class DataExportStatusRequest
{
    /// <summary>
    /// The export job ID.
    /// </summary>
    public int Id { get; set; }
}

/// <summary>
/// Response model for the data export status endpoint.
/// </summary>
public sealed class DataExportStatusResponse
{
    /// <summary>
    /// The export job ID.
    /// </summary>
    public int JobId { get; set; }

    /// <summary>
    /// The current status of the export job.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// URL for authenticated download (requires TenantAdmin login).
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// Shareable URL that works without authentication (token-based).
    /// </summary>
    public string? ShareableUrl { get; set; }

    /// <summary>
    /// When the export file expires and will be deleted.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Error message if the job failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Size of the exported file in bytes.
    /// </summary>
    public long? FileSizeBytes { get; set; }
}

/// <summary>
/// Returns the status and download URLs for a data export job.
/// </summary>
public sealed class DataExportStatusEndpoint : Endpoint<DataExportStatusRequest, DataExportStatusResponse>
{
    private readonly IDataExportHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="DataExportStatusEndpoint"/> class.
    /// </summary>
    public DataExportStatusEndpoint(IDataExportHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/tenants/export/{Id}");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(DataExportStatusRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        ServiceResult<DataExportJob> result = await _handler.GetExportJobAsync(req.Id, tenantId, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<DataExportStatusResponse>.Error("Export job not found"), ct);

            return;
        }

        DataExportJob job = result.Data!;

        string? downloadUrl = null;
        string? shareableUrl = null;

        if (job.Status == DataExportJobStatus.Complete)
        {
            downloadUrl = $"/v1/api/tenants/export/{job.Id}/download";
            shareableUrl = $"/v1/api/exports/download?token={job.DownloadToken}";
        }

        await Send.OkAsync(new DataExportStatusResponse
        {
            JobId = job.Id,
            Status = job.Status.ToString(),
            DownloadUrl = downloadUrl,
            ShareableUrl = shareableUrl,
            ExpiresAt = job.ExpiresAt,
            ErrorMessage = job.ErrorMessage,
            FileSizeBytes = job.FileSizeBytes
        }, cancellation: ct);
    }
}
