// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Models.Users;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Users;

/// <summary>
/// Returns a list of user accounts.
/// </summary>
public sealed class UserListEndpoint : EndpointWithoutRequest<ApiResponse<List<UserAccountDto>>>
{
    private readonly IUserHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="UserListEndpoint"/> class.
    /// </summary>
    public UserListEndpoint(IUserHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/users");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        ServiceResult<List<UserAccountDto>> result = await _handler.ListAsync(tenantId, ct);

        await Send.OkAsync(ApiResponse<List<UserAccountDto>>.Ok(result.Data ?? []), cancellation: ct);
    }
}
