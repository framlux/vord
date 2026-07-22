// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Hangfire;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Response model for the data export request endpoint.
/// </summary>
public sealed class RequestDataExportResponse
{
    /// <summary>
    /// The ID of the created export job.
    /// </summary>
    public int JobId { get; set; }

    /// <summary>
    /// The initial status of the job.
    /// </summary>
    public string Status { get; set; } = "Pending";
}

/// <summary>
/// Creates a pending data export job for the current tenant.
/// </summary>
public sealed class RequestDataExportEndpoint : EndpointWithoutRequest<ApiResponse<RequestDataExportResponse>>
{
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IDataExportHandler _handler;
    private readonly IObjectStorageService _objectStorageService;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="RequestDataExportEndpoint"/> class.
    /// </summary>
    public RequestDataExportEndpoint(
        IDataExportHandler handler,
        IObjectStorageService objectStorageService,
        IBackgroundJobClient backgroundJobClient,
        ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(objectStorageService);
        ArgumentNullException.ThrowIfNull(backgroundJobClient);
        ArgumentNullException.ThrowIfNull(tenantContext);

        _handler = handler;
        _objectStorageService = objectStorageService;
        _backgroundJobClient = backgroundJobClient;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/tenants/export");
        Policies(AuthorizationPolicies.TenantAdmin);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        if (_objectStorageService is NoOpObjectStorageService)
        {
            await HttpContext.SendApiErrorAsync(StatusCodes.Status501NotImplemented, "Data export is not available on this server", ct);

            return;
        }

        int? tenantId = _tenantContext.TenantId;

        int? userId = _tenantContext.UserId;
        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(StatusCodes.Status401Unauthorized, "Unable to identify user", ct);

            return;
        }

        ServiceResult<int> result = await _handler.ExportTenantDataAsync(tenantId, userId.Value, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(StatusCodes.Status404NotFound, "Tenant not found", ct);

            return;
        }

        if (result.StatusCode == StatusCodes.Status409Conflict)
        {
            await HttpContext.SendApiErrorAsync(StatusCodes.Status409Conflict, "A data export is already in progress", ct);

            return;
        }

        // Enqueue the per-job claim path so we process exactly this row, not a fleet-wide sweep.
        // The recurring DataExportProcessingJob.RunAsync continues to run as the orphan reaper.
        _backgroundJobClient.Enqueue<DataExportProcessingJob>(job => job.ProcessSingleAsync(result.Data, CancellationToken.None));

        await Send.OkAsync(
            ApiResponse<RequestDataExportResponse>.Ok(new RequestDataExportResponse { JobId = result.Data, Status = "Pending" }),
            cancellation: ct);
    }
}
