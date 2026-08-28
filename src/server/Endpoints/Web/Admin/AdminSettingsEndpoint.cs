// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Admin;

/// <summary>
/// Returns server configuration settings. Only available in a self-hosted deployment; in SaaS
/// these settings are read from the internal admin application.
/// </summary>
public sealed class AdminSettingsEndpoint : EndpointWithoutRequest<ApiResponse<ServerSettingsDto>>
{
    private readonly DeploymentMode _deploymentMode;
    private readonly AdminHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="AdminSettingsEndpoint"/> class.
    /// </summary>
    public AdminSettingsEndpoint(DeploymentMode deploymentMode, AdminHandler handler)
    {
        _deploymentMode = deploymentMode;
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/admin/settings");
        Policies(AuthorizationPolicies.Admin);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await AdminEndpointGuards.RefusedForDeploymentAsync(HttpContext, _deploymentMode, ct))
        {
            return;
        }

        ServiceResult<List<SettingEntry>> result = await _handler.GetSettingsAsync(ct);

        ServerSettingsDto dto = new()
        {
            Settings = result.Data ?? [],
        };

        await Send.OkAsync(ApiResponse<ServerSettingsDto>.Ok(dto), cancellation: ct);
    }
}
