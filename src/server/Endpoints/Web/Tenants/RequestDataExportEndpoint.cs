// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

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
public sealed class RequestDataExportEndpoint : EndpointWithoutRequest<RequestDataExportResponse>
{
    private readonly IDataExportHandler _handler;
    private readonly IObjectStorageService _objectStorageService;

    /// <summary>
    /// Creates a new instance of the <see cref="RequestDataExportEndpoint"/> class.
    /// </summary>
    public RequestDataExportEndpoint(IDataExportHandler handler, IObjectStorageService objectStorageService)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(objectStorageService);

        _handler = handler;
        _objectStorageService = objectStorageService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/tenants/export");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        if (_objectStorageService is NoOpObjectStorageService)
        {
            HttpContext.Response.StatusCode = 501;
            await HttpContext.Response.WriteAsJsonAsync(
                new RequestDataExportResponse { JobId = 0, Status = "NotAvailable" }, ct);

            return;
        }

        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<RequestDataExportResponse>.Error("Unable to identify user"), ct);

            return;
        }

        ServiceResult<int> result = await _handler.ExportTenantDataAsync(tenantId, userId.Value, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<RequestDataExportResponse>.Error("Tenant not found"), ct);

            return;
        }

        if (result.StatusCode == 409)
        {
            HttpContext.Response.StatusCode = 409;
            await HttpContext.Response.WriteAsJsonAsync(
                new RequestDataExportResponse { JobId = 0, Status = "AlreadyInProgress" }, ct);

            return;
        }

        await Send.OkAsync(new RequestDataExportResponse { JobId = result.Data, Status = "Pending" }, cancellation: ct);
    }
}
