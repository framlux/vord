// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.DataExport;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Request model for the token-based export download endpoint.
/// </summary>
public sealed class DataExportTokenDownloadRequest
{
    /// <summary>
    /// The download token for pre-authenticated access.
    /// </summary>
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// Streams an export file using a pre-authenticated download token.
/// No login required — the token IS the authorization.
/// </summary>
public sealed class DataExportTokenDownloadEndpoint : Endpoint<DataExportTokenDownloadRequest>
{
    private readonly IDataExportHandler _handler;
    private readonly IObjectStorageService _objectStorageService;

    /// <summary>
    /// Creates a new instance of the <see cref="DataExportTokenDownloadEndpoint"/> class.
    /// </summary>
    public DataExportTokenDownloadEndpoint(IDataExportHandler handler, IObjectStorageService objectStorageService)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(objectStorageService);

        _handler = handler;
        _objectStorageService = objectStorageService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/exports/download");
        AllowAnonymous();
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(DataExportTokenDownloadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Token))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                new { message = "Token is required" }, ct);

            return;
        }

        ServiceResult<DataExportJob> result = await _handler.GetExportJobByTokenAsync(req.Token, ct);

        if (result.IsNotFound)
        {
            await Send.NotFoundAsync(ct);

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
