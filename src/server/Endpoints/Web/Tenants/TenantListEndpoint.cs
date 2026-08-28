// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Services.Core.Auth;
using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Tenants;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Returns a list of tenants.
/// </summary>
public sealed class TenantListEndpoint : EndpointWithoutRequest<ApiResponse<List<TenantDto>>>
{
    private readonly DeploymentMode _deploymentMode;
    private readonly TenantHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="TenantListEndpoint"/> class.
    /// </summary>
    public TenantListEndpoint(DeploymentMode deploymentMode, TenantHandler handler)
    {
        _deploymentMode = deploymentMode;
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/tenants");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        // Global admins see all tenants — but only where a fleet-local administration console
        // exists to show them. This route is also the ordinary tenant switcher, so it stays open
        // in both modes; it is the cross-tenant escalation that is scoped away in SaaS, where the
        // whole-fleet view belongs to the internal operator application. Without this the admin
        // page's tenant list survives the removal of the page as a plain GET.
        bool isGlobalAdmin = AuthClaims.IsUserGlobalAdmin(User) && _deploymentMode.IsSelfHosted;

        List<int> tenantIds = User.FindAll(ClaimTypes.Role)
            .Select(c => c.Value.Split(':'))
            .Where(parts => parts.Length == 2 && int.TryParse(parts[0], out _))
            .Select(parts => int.Parse(parts[0]))
            .Distinct()
            .ToList();

        ServiceResult<List<TenantDto>> result = await _handler.ListForUserAsync(isGlobalAdmin, tenantIds, ct);

        await Send.OkAsync(ApiResponse<List<TenantDto>>.Ok(result.Data ?? []), cancellation: ct);
    }
}
