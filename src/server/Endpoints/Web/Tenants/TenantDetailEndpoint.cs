// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Tenants;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Returns details about a specific tenant.
/// </summary>
public sealed class TenantDetailEndpoint : EndpointWithoutRequest<ApiResponse<TenantDto>>
{
    private readonly ITenantHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="TenantDetailEndpoint"/> class.
    /// </summary>
    public TenantDetailEndpoint(ITenantHandler handler)
    {
        _handler = handler;
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
        string? iga = User.FindFirstValue("iga");
        bool isGlobalAdmin = string.Equals(iga, bool.TrueString, StringComparison.OrdinalIgnoreCase);

        if (isGlobalAdmin == false)
        {
            int? claimTenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
            if ((claimTenantId is null) || (claimTenantId.Value != tenantId))
            {
                HttpContext.Response.StatusCode = 404;
                await HttpContext.Response.WriteAsJsonAsync(
                    ApiResponse<TenantDto>.Error("Tenant not found"), ct);

                return;
            }
        }

        ServiceResult<TenantDto> result = await _handler.GetDetailAsync(tenantId, ct);
        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<TenantDto>.Error("Tenant not found"), ct);

            return;
        }

        await Send.OkAsync(ApiResponse<TenantDto>.Ok(result.Data!), cancellation: ct);
    }
}
