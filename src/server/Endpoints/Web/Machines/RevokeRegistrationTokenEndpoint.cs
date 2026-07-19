// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Revokes a registration token.
/// </summary>
public sealed class RevokeRegistrationTokenEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    private readonly IRegistrationTokenHandler _handler;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="RevokeRegistrationTokenEndpoint"/> class.
    /// </summary>
    public RevokeRegistrationTokenEndpoint(IRegistrationTokenHandler handler, ITenantContext tenantContext)
    {
        _handler = handler;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Delete("/machines/registration-tokens/{id}");
        Policies("MachineAdmin");
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long tokenId = Route<long>("id");
        int tenantId = _tenantContext.RequireTenantId();

        int? userId = _tenantContext.UserId;
        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        ServiceResult<object> result = await _handler.RevokeAsync(tokenId, tenantId, userId.Value, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Registration token not found", ct);

            return;
        }

        await Send.OkAsync(ApiResponse<object>.Ok(new { }, "Token revoked"), cancellation: ct);
    }
}
