// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

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
        Post("/machines/registration-tokens");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CreateRegistrationTokenRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<RegistrationTokenDto>.Error("Unable to identify tenant"), ct);

            return;
        }

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<RegistrationTokenDto>.Error("Unable to identify user"), ct);

            return;
        }

        ServiceResult<RegistrationTokenDto> result = await _handler.CreateAsync(
            tenantId.Value, userId.Value, req.Name, ct);

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<RegistrationTokenDto>.Error("Validation failed"), ct);

            return;
        }

        await Send.OkAsync(ApiResponse<RegistrationTokenDto>.Ok(result.Data!), cancellation: ct);
    }
}
