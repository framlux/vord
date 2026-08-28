// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Models.Users;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Admin;

/// <summary>
/// Returns all users across all tenants (admin only). Only available in a self-hosted deployment;
/// in SaaS this data is read from the internal admin application.
/// </summary>
public sealed class AdminUsersEndpoint : EndpointWithoutRequest<ApiResponse<List<UserAccountDto>>>
{
    private readonly DeploymentMode _deploymentMode;
    private readonly AdminHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="AdminUsersEndpoint"/> class.
    /// </summary>
    public AdminUsersEndpoint(DeploymentMode deploymentMode, AdminHandler handler)
    {
        _deploymentMode = deploymentMode;
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/admin/users");
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

        ServiceResult<List<UserAccountDto>> result = await _handler.GetAllUsersAsync(ct);

        await Send.OkAsync(ApiResponse<List<UserAccountDto>>.Ok(result.Data ?? []), cancellation: ct);
    }
}
