// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Models.History;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;

/// <summary>
/// Shared validation logic for all history endpoints. Handles tenant auth,
/// machine lookup, range validation, and retention tier gating.
/// </summary>
public sealed class HistoryRequestValidator
{
    private readonly IMachineRepository _machineRepo;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="HistoryRequestValidator"/> class.
    /// </summary>
    public HistoryRequestValidator(
        IMachineRepository machineRepo,
        ISubscriptionService subscriptionService)
    {
        _machineRepo = machineRepo;
        _subscriptionService = subscriptionService;
    }

    /// <summary>
    /// Validates a history request and returns a resolved context, or writes an error
    /// response and returns null. When this method returns null, the caller should return
    /// immediately without writing any further response.
    /// </summary>
    /// <param name="machineId">The machine ID from the route.</param>
    /// <param name="range">The time range query parameter.</param>
    /// <param name="httpContext">The current HTTP context for claims and response writing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A validated context, or null if an error response was written.</returns>
    public async Task<HistoryRequestContext?> ValidateAsync(
        long machineId,
        string? range,
        HttpContext httpContext,
        CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(httpContext.User, httpContext);

        if (tenantId is null)
        {
            httpContext.Response.StatusCode = 403;
            await httpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Forbidden"), ct);

            return null;
        }

        if (await _machineRepo.GetActiveMachineByIdAsync(machineId, tenantId.Value, ct) is null)
        {
            httpContext.Response.StatusCode = 404;
            await httpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Machine not found"), ct);

            return null;
        }

        int retentionDays = await _subscriptionService.GetRetentionDaysForTenantAsync(tenantId.Value, ct);

        HistoryRangeResult rangeResult = HistoryTimeRange.TryResolve(
            range, retentionDays,
            out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd,
            out string rangeError);

        if (rangeResult == HistoryRangeResult.InvalidRange)
        {
            httpContext.Response.StatusCode = 400;
            await httpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error(rangeError), ct);

            return null;
        }

        if (rangeResult == HistoryRangeResult.RetentionExceeded)
        {
            httpContext.Response.StatusCode = 403;
            await httpContext.Response.WriteAsJsonAsync(new HistoryRetentionErrorDto
            {
                Message = rangeError,
                UpgradeRequired = true,
                CurrentRetentionDays = retentionDays
            }, ct);

            return null;
        }

        return new HistoryRequestContext
        {
            MachineId = machineId,
            TenantId = tenantId.Value,
            RangeStart = rangeStart,
            RangeEnd = rangeEnd
        };
    }
}
