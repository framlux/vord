// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Lists registration tokens for the tenant (paginated).
/// </summary>
public sealed class ListRegistrationTokensEndpoint : EndpointWithoutRequest<ApiResponse<PaginatedResponse<RegistrationTokenDto>>>
{
    private readonly RegistrationTokenHandler _handler;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="ListRegistrationTokensEndpoint"/> class.
    /// </summary>
    public ListRegistrationTokensEndpoint(RegistrationTokenHandler handler, ITenantContext tenantContext)
    {
        _handler = handler;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/registration-tokens");
        Policies("MachineAdmin");
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int tenantId = _tenantContext.RequireTenantId();

        int page = Math.Max(1, Query<int?>("page", isRequired: false) ?? 1);
        int pageSize = Math.Clamp(Query<int?>("pageSize", isRequired: false) ?? 25, 1, 100);

        ServiceResult<PaginatedResponse<RegistrationTokenDto>> result = await _handler.ListAsync(tenantId, page, pageSize, ct);

        if (result.IsSuccess == false)
        {
            await HttpContext.SendApiErrorAsync(result.StatusCode, "Failed to retrieve tokens", ct);

            return;
        }

        await Send.OkAsync(ApiResponse<PaginatedResponse<RegistrationTokenDto>>.Ok(result.Data!), cancellation: ct);
    }
}
