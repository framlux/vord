// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Updates the OIDC configuration for a tenant.
/// </summary>
public sealed class UpdateTenantOidcConfigEndpoint : Endpoint<TenantOidcConfigDto, ApiResponse<TenantOidcConfigDto>>
{
    private readonly ITenantOidcHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="UpdateTenantOidcConfigEndpoint"/> class.
    /// </summary>
    public UpdateTenantOidcConfigEndpoint(ITenantOidcHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Put("/tenants/{id}/oidc");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(TenantOidcConfigDto req, CancellationToken ct)
    {
        int tenantId = Route<int>("id");
        int? claimTenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        ServiceResult<TenantOidcConfigDto> result = await _handler.UpdateConfigAsync(tenantId, claimTenantId, req, ct);

        if (result.IsNotFound)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        if (result.StatusCode == 403)
        {
            HttpContext.Response.StatusCode = 403;
            await Send.OkAsync(ApiResponse<TenantOidcConfigDto>.Error("Custom OIDC is only available on the Team tier"), cancellation: ct);

            return;
        }

        if (result.StatusCode == 400)
        {
            HttpContext.Response.StatusCode = 400;
            await Send.OkAsync(ApiResponse<TenantOidcConfigDto>.Error("Authority URL must be a valid HTTPS URL pointing to a public address"), cancellation: ct);

            return;
        }

        await Send.OkAsync(ApiResponse<TenantOidcConfigDto>.Ok(result.Data!, "OIDC configuration updated"), cancellation: ct);
    }
}
