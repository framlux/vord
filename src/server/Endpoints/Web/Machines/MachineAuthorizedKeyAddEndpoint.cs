// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Request to authorize a signing key for a machine.
/// </summary>
public sealed class MachineAuthorizedKeyAddRequest
{
    /// <summary>
    /// The ID of the signing key to authorize.
    /// </summary>
    public int SigningKeyId { get; set; }
}

/// <summary>
/// Authorizes a signing key for a specific machine, enabling remote command execution.
/// </summary>
public sealed class MachineAuthorizedKeyAddEndpoint : Endpoint<MachineAuthorizedKeyAddRequest, ApiResponse<MachineAuthorizedKeyDto>>
{
    private readonly IMachineAuthorizedKeyService _authorizedKeyService;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineAuthorizedKeyAddEndpoint"/> class.
    /// </summary>
    public MachineAuthorizedKeyAddEndpoint(IMachineAuthorizedKeyService authorizedKeyService, ISubscriptionService subscriptionService)
    {
        _authorizedKeyService = authorizedKeyService;
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/machines/{machineId}/authorized-keys");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(MachineAuthorizedKeyAddRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<MachineAuthorizedKeyDto>.Error("Unable to identify tenant"), ct);

            return;
        }

        // Remote commands require a Team subscription.
        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier != SubscriptionTier.Team))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<MachineAuthorizedKeyDto>.Error("Remote commands require a Team subscription"), ct);

            return;
        }

        long machineId = Route<long>("machineId");

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<MachineAuthorizedKeyDto>.Error("Unable to identify user"), ct);

            return;
        }

        ServiceResult<MachineAuthorizedKey> result = await _authorizedKeyService.AuthorizeKeyAsync(
            machineId, req.SigningKeyId, userId.Value, tenantId.Value, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<MachineAuthorizedKeyDto>.Error("Machine or signing key not found"), ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<MachineAuthorizedKeyDto>.Error(result.ErrorMessage ?? "Authorization failed"), ct);

            return;
        }

        if (result.Data is null)
        {
            HttpContext.Response.StatusCode = 500;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<MachineAuthorizedKeyDto>.Error("Unexpected null result"), ct);

            return;
        }

        // Re-fetch the full list for this machine to get joined display data for the response.
        ServiceResult<List<MachineAuthorizedKeyDto>> listResult = await _authorizedKeyService.ListAuthorizedKeysAsync(
            machineId, tenantId.Value, ct);

        MachineAuthorizedKeyDto? dto = listResult.Data?.Find(k => k.Id == result.Data.Id);
        if (dto is null)
        {
            dto = new MachineAuthorizedKeyDto
            {
                Id = result.Data.Id,
                SigningKeyId = result.Data.SigningKeyId,
                AuthorizedAt = result.Data.AuthorizedAt,
                IsActive = true,
            };
        }

        await Send.OkAsync(ApiResponse<MachineAuthorizedKeyDto>.Ok(dto), cancellation: ct);
    }
}
