// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Models.Tenants;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Request model for creating a tenant.
/// </summary>
public sealed class CreateTenantRequest
{
    /// <summary>
    /// The tenant name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The tenant logo URL.
    /// </summary>
    public string LogoUrl { get; set; } = string.Empty;
}

/// <summary>
/// Creates a new tenant.
/// </summary>
public sealed class TenantCreateEndpoint : Endpoint<CreateTenantRequest, ApiResponse<TenantDto>>
{
    private readonly ITenantHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="TenantCreateEndpoint"/> class.
    /// </summary>
    public TenantCreateEndpoint(ITenantHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/tenants");
        Policies("Admin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CreateTenantRequest req, CancellationToken ct)
    {
        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<TenantDto>.Error("Unable to identify user"), ct);

            return;
        }

        ServiceResult<TenantDto> result = await _handler.CreateAsync(req.Name, req.LogoUrl, userId.Value, ct);

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            string message = result.StatusCode == 409
                ? "A tenant with this name already exists"
                : "Tenant name is required";
            await Send.OkAsync(ApiResponse<TenantDto>.Error(message), cancellation: ct);

            return;
        }

        await Send.CreatedAtAsync<TenantDetailEndpoint>(new { id = result.Data!.Id }, ApiResponse<TenantDto>.Ok(result.Data), cancellation: ct);
    }
}
