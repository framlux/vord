// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Gets the OIDC configuration for a tenant.
/// </summary>
public sealed class GetTenantOidcConfigEndpoint : EndpointWithoutRequest<ApiResponse<TenantOidcConfigDto>>
{
    private readonly ITenantOidcHandler _handler;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="GetTenantOidcConfigEndpoint"/> class.
    /// </summary>
    public GetTenantOidcConfigEndpoint(ITenantOidcHandler handler, ITenantContext tenantContext)
    {
        _handler = handler;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/tenants/{id}/oidc");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int tenantId = Route<int>("id");
        int? claimTenantId = _tenantContext.TenantId;

        ServiceResult<TenantOidcConfigDto> result = await _handler.GetConfigAsync(tenantId, claimTenantId, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Tenant not found", ct);

            return;
        }

        if (result.StatusCode == 403)
        {
            await HttpContext.SendApiErrorAsync(403, "Custom OIDC is only available on the Team tier", ct);

            return;
        }

        await Send.OkAsync(ApiResponse<TenantOidcConfigDto>.Ok(result.Data!), cancellation: ct);
    }
}
