// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Auth;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Tenants;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Returns details about a specific tenant.
/// </summary>
public sealed class TenantDetailEndpoint : EndpointWithoutRequest<ApiResponse<TenantDto>>
{
    private readonly TenantHandler _handler;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="TenantDetailEndpoint"/> class.
    /// </summary>
    public TenantDetailEndpoint(TenantHandler handler, ITenantContext tenantContext)
    {
        _handler = handler;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/tenants/{id}");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int tenantId = Route<int>("id");

        // Global admins may view any tenant; non-admins can only view their own
        bool isGlobalAdmin = AuthClaims.IsUserGlobalAdmin(User);

        if (isGlobalAdmin == false)
        {
            int? claimTenantId = _tenantContext.TenantId;
            if ((claimTenantId is null) || (claimTenantId.Value != tenantId))
            {
                await HttpContext.SendApiErrorAsync(404, "Tenant not found", ct);

                return;
            }
        }

        ServiceResult<TenantDto> result = await _handler.GetDetailAsync(tenantId, ct);
        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Tenant not found", ct);

            return;
        }

        await Send.OkAsync(ApiResponse<TenantDto>.Ok(result.Data!), cancellation: ct);
    }
}
