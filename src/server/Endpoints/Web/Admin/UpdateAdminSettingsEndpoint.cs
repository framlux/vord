// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Admin;

/// <summary>
/// Updates server configuration settings. Only available when billing is disabled.
/// </summary>
public sealed class UpdateAdminSettingsEndpoint : Endpoint<UpdateAdminSettingsRequest, ApiResponse<ServerSettingsDto>>
{
    private readonly IBillingStatus _billingStatus;
    private readonly IAdminHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="UpdateAdminSettingsEndpoint"/> class.
    /// </summary>
    public UpdateAdminSettingsEndpoint(IBillingStatus billingStatus, IAdminHandler handler)
    {
        _billingStatus = billingStatus;
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Put("/admin/settings");
        Policies("Admin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(UpdateAdminSettingsRequest req, CancellationToken ct)
    {
        if (_billingStatus.IsEnabled)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<ServerSettingsDto>.Error("Endpoint not available when billing is enabled"), ct);

            return;
        }

        ServiceResult<List<SettingEntry>> result = await _handler.UpdateSettingsAsync(req.Settings, ct);

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<ServerSettingsDto>.Error(result.ErrorMessage ?? "Unknown error"), ct);

            return;
        }

        ServerSettingsDto dto = new()
        {
            Settings = result.Data ?? [],
        };

        await Send.OkAsync(ApiResponse<ServerSettingsDto>.Ok(dto), cancellation: ct);
    }
}
