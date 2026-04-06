// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Creates a new registration token for the tenant.
/// </summary>
public sealed class CreateRegistrationTokenEndpoint : Endpoint<CreateRegistrationTokenRequest, ApiResponse<RegistrationTokenDto>>
{
    private readonly IRegistrationTokenHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="CreateRegistrationTokenEndpoint"/> class.
    /// </summary>
    public CreateRegistrationTokenEndpoint(IRegistrationTokenHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/tenants/registration-tokens");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CreateRegistrationTokenRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        string? userIdStr = User.FindFirstValue(ClaimTypes.Actor);
        int userId = int.TryParse(userIdStr, out int uid) ? uid : 0;

        ServiceResult<RegistrationTokenDto> result = await _handler.CreateAsync(
            tenantId.Value, userId, req.Name, ct);

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await Send.OkAsync(ApiResponse<RegistrationTokenDto>.Error("Validation failed"), cancellation: ct);

            return;
        }

        await Send.OkAsync(ApiResponse<RegistrationTokenDto>.Ok(result.Data!), cancellation: ct);
    }
}
