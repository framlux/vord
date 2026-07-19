// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

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
        // Cap anonymous token-brute-force at 30/min per IP. The token has 256 bits of
        // entropy so brute force is not practical, but the rate limit prevents DB load from
        // sustained probing and gives the operator a visible signal in the metrics.
        Options(x => x.RequireRateLimiting("anonymous-token"));
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(DataExportTokenDownloadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Token))
        {
            await HttpContext.SendApiErrorAsync(400, "Token is required", ct);

            return;
        }

        ServiceResult<DataExportJob> result = await _handler.GetExportJobByTokenAsync(req.Token, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Export not found or token is invalid", ct);

            return;
        }

        DataExportJob job = result.Data!;

        if (job.Status != DataExportJobStatus.Complete)
        {
            await HttpContext.SendApiErrorAsync(409, $"Export is not ready for download (status: {job.Status})", ct);

            return;
        }

        if (job.ExpiresAt < DateTimeOffset.UtcNow)
        {
            await HttpContext.SendApiErrorAsync(410, "Export has expired", ct);

            return;
        }

        if (string.IsNullOrEmpty(job.ObjectKey))
        {
            await HttpContext.SendApiErrorAsync(500, "Export file not found", ct);

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
