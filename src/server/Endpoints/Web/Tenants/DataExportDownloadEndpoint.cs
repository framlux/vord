// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.DataExport;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Request model for the data export download endpoint.
/// </summary>
public sealed class DataExportDownloadRequest
{
    /// <summary>
    /// The export job ID.
    /// </summary>
    public int Id { get; set; }
}

/// <summary>
/// Streams an export file to the authenticated user. Enforces tenant isolation.
/// </summary>
public sealed class DataExportDownloadEndpoint : Endpoint<DataExportDownloadRequest>
{
    private readonly IDataExportHandler _handler;
    private readonly IObjectStorageService _objectStorageService;

    /// <summary>
    /// Creates a new instance of the <see cref="DataExportDownloadEndpoint"/> class.
    /// </summary>
    public DataExportDownloadEndpoint(IDataExportHandler handler, IObjectStorageService objectStorageService)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(objectStorageService);

        _handler = handler;
        _objectStorageService = objectStorageService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/tenants/export/{Id}/download");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(DataExportDownloadRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        ServiceResult<DataExportJob> result = await _handler.GetExportJobAsync(req.Id, tenantId, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Export not found"), ct);

            return;
        }

        DataExportJob job = result.Data!;

        if (job.Status != DataExportJobStatus.Complete)
        {
            HttpContext.Response.StatusCode = 409;
            await HttpContext.Response.WriteAsJsonAsync(
                new { message = "Export is not ready for download", status = job.Status.ToString() }, ct);

            return;
        }

        if (job.ExpiresAt < DateTimeOffset.UtcNow)
        {
            HttpContext.Response.StatusCode = 410;
            await HttpContext.Response.WriteAsJsonAsync(
                new { message = "Export has expired" }, ct);

            return;
        }

        if (string.IsNullOrEmpty(job.ObjectKey))
        {
            HttpContext.Response.StatusCode = 500;
            await HttpContext.Response.WriteAsJsonAsync(
                new { message = "Export file not found" }, ct);

            return;
        }

        await StreamExportFileAsync(job, ct);
    }

    private async Task StreamExportFileAsync(DataExportJob job, CancellationToken ct)
    {
        await using Stream objectStream = await _objectStorageService.GetObjectStreamAsync(job.ObjectKey, ct);

        HttpContext.Response.ContentType = "application/x-sqlite3";
        HttpContext.Response.Headers.ContentDisposition =
            $"attachment; filename=\"vord-export-{job.TenantId}-{job.Id}.sqlite\"";

        if (job.FileSizeBytes.HasValue)
        {
            HttpContext.Response.ContentLength = job.FileSizeBytes.Value;
        }

        await objectStream.CopyToAsync(HttpContext.Response.Body, ct);
    }
}
