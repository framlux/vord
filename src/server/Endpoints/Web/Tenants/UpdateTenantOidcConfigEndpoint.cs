// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Tenants;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Updates the OIDC configuration for a tenant.
/// </summary>
public sealed class UpdateTenantOidcConfigEndpoint : Endpoint<TenantOidcConfigDto, ApiResponse<TenantOidcConfigDto>>
{
    private readonly TenantOidcHandler _handler;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="UpdateTenantOidcConfigEndpoint"/> class.
    /// </summary>
    public UpdateTenantOidcConfigEndpoint(TenantOidcHandler handler, ITenantContext tenantContext)
    {
        _handler = handler;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Put("/tenants/{id}/oidc");
        Policies(AuthorizationPolicies.TenantAdmin);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(TenantOidcConfigDto req, CancellationToken ct)
    {
        int tenantId = Route<int>("id");
        int? claimTenantId = _tenantContext.TenantId;
        int? userId = _tenantContext.UserId;

        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        ServiceResult<TenantOidcConfigDto> result = await _handler.UpdateConfigAsync(tenantId, claimTenantId, userId.Value, req, ct);

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

        if (result.StatusCode == 400)
        {
            await HttpContext.SendApiErrorAsync(400, "Authority URL must be a valid HTTPS URL pointing to a public address", ct);

            return;
        }

        await Send.OkAsync(ApiResponse<TenantOidcConfigDto>.Ok(result.Data!, "OIDC configuration updated"), cancellation: ct);
    }
}
