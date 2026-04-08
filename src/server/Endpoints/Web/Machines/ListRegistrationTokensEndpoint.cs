// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Lists registration tokens for the tenant (paginated).
/// </summary>
public sealed class ListRegistrationTokensEndpoint : EndpointWithoutRequest<ApiResponse<PaginatedResponse<RegistrationTokenDto>>>
{
    private readonly IRegistrationTokenHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="ListRegistrationTokensEndpoint"/> class.
    /// </summary>
    public ListRegistrationTokensEndpoint(IRegistrationTokenHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/registration-tokens");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        int page = Math.Max(1, Query<int?>("page", isRequired: false) ?? 1);
        int pageSize = Math.Clamp(Query<int?>("pageSize", isRequired: false) ?? 25, 1, 100);

        ServiceResult<PaginatedResponse<RegistrationTokenDto>> result = await _handler.ListAsync(tenantId.Value, page, pageSize, ct);

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<PaginatedResponse<RegistrationTokenDto>>.Error("Failed to retrieve tokens"), ct);

            return;
        }

        await Send.OkAsync(ApiResponse<PaginatedResponse<RegistrationTokenDto>>.Ok(result.Data!), cancellation: ct);
    }
}
