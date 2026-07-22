// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Admin;

/// <summary>
/// Updates server configuration settings. Only available when billing is disabled.
/// </summary>
public sealed class UpdateAdminSettingsEndpoint : Endpoint<UpdateAdminSettingsRequest, ApiResponse<ServerSettingsDto>>
{
    private readonly BillingStatus _billingStatus;
    private readonly AdminHandler _handler;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="UpdateAdminSettingsEndpoint"/> class.
    /// </summary>
    public UpdateAdminSettingsEndpoint(BillingStatus billingStatus, AdminHandler handler, ITenantContext tenantContext)
    {
        _billingStatus = billingStatus;
        _handler = handler;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Put("/admin/settings");
        Policies(AuthorizationPolicies.Admin);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(UpdateAdminSettingsRequest req, CancellationToken ct)
    {
        if (_billingStatus.IsEnabled)
        {
            await HttpContext.SendApiErrorAsync(404, "Endpoint not available when billing is enabled", ct);

            return;
        }

        int? userId = _tenantContext.UserId;
        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        ServiceResult<List<SettingEntry>> result = await _handler.UpdateSettingsAsync(req.Settings, userId.Value, ct);

        if (result.IsSuccess == false)
        {
            await HttpContext.SendApiErrorAsync(result.StatusCode, result.ErrorMessage ?? "Unknown error", ct);

            return;
        }

        ServerSettingsDto dto = new()
        {
            Settings = result.Data ?? [],
        };

        await Send.OkAsync(ApiResponse<ServerSettingsDto>.Ok(dto), cancellation: ct);
    }
}
