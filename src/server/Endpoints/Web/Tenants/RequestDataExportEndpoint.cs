// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Globalization;
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
/// Response model returned when an export is refused because the tenant's cooldown has not elapsed.
/// </summary>
public sealed class RequestDataExportThrottledResponse
{
    /// <summary>
    /// When the tenant may next generate an export, as an ISO-8601 timestamp. Present so the
    /// dashboard can say when rather than only that the request was refused.
    /// </summary>
    public string NextExportAvailableAt { get; set; } = "";
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
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="RequestDataExportEndpoint"/> class.
    /// </summary>
    public RequestDataExportEndpoint(
        IDataExportHandler handler,
        IObjectStorageService objectStorageService,
        IBackgroundJobClient backgroundJobClient,
        ITenantContext tenantContext,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(objectStorageService);
        ArgumentNullException.ThrowIfNull(backgroundJobClient);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _handler = handler;
        _objectStorageService = objectStorageService;
        _backgroundJobClient = backgroundJobClient;
        _tenantContext = tenantContext;
        _timeProvider = timeProvider;
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

        ServiceResult<DataExportRequestOutcome> result = await _handler.ExportTenantDataAsync(tenantId, userId.Value, ct);

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

        if (result.StatusCode == StatusCodes.Status429TooManyRequests)
        {
            await SendCooldownAsync(result.Data?.NextEligibleAt, ct);

            return;
        }

        // Enqueue the per-job claim path so we process exactly this row, not a fleet-wide sweep.
        // The recurring DataExportProcessingJob.RunAsync continues to run as the orphan reaper.
        int jobId = result.Data!.JobId;

        _backgroundJobClient.Enqueue<DataExportProcessingJob>(job => job.ProcessSingleAsync(jobId, CancellationToken.None));

        await Send.OkAsync(
            ApiResponse<RequestDataExportResponse>.Ok(new RequestDataExportResponse { JobId = jobId, Status = "Pending" }),
            cancellation: ct);
    }

    /// <summary>
    /// Writes the throttled response. The eligibility instant comes from the same decision that
    /// refused the request, so the header and the body can never disagree with the refusal or
    /// with each other.
    /// </summary>
    private async Task SendCooldownAsync(DateTimeOffset? nextEligibleAt, CancellationToken ct)
    {
        HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (nextEligibleAt is not null)
        {
            // Retry-After is whole seconds, rounded up so it never points at an instant that is
            // still inside the window.
            double secondsRemaining = (nextEligibleAt.Value - _timeProvider.GetUtcNow()).TotalSeconds;
            long retryAfter = (long)Math.Max(1, Math.Ceiling(secondsRemaining));

            HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        }

        await HttpContext.Response.WriteAsJsonAsync(
            new ApiResponse<RequestDataExportThrottledResponse>
            {
                Success = false,
                Message = nextEligibleAt is null
                    ? "Another data export cannot be requested yet"
                    : $"Another data export cannot be requested until {nextEligibleAt.Value:o}",
                Data = new RequestDataExportThrottledResponse
                {
                    NextExportAvailableAt = nextEligibleAt?.ToString("o") ?? "",
                },
            },
            ct);
    }
}
