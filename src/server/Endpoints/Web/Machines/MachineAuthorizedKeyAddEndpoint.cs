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
    private readonly MachineAuthorizedKeyService _authorizedKeyService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineAuthorizedKeyAddEndpoint"/> class.
    /// </summary>
    public MachineAuthorizedKeyAddEndpoint(MachineAuthorizedKeyService authorizedKeyService, ISubscriptionService subscriptionService, ITenantContext tenantContext)
    {
        _authorizedKeyService = authorizedKeyService;
        _subscriptionService = subscriptionService;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/machines/{machineId}/authorized-keys");
        Policies("MachineAdmin");
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(MachineAuthorizedKeyAddRequest req, CancellationToken ct)
    {
        int tenantId = _tenantContext.RequireTenantId();

        // Remote commands require a Team subscription.
        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
        if ((subscription is null) || (subscription.Tier != SubscriptionTier.Team))
        {
            await HttpContext.SendApiErrorAsync(403, "Remote commands require a Team subscription", ct);

            return;
        }

        long machineId = Route<long>("machineId");

        int? userId = _tenantContext.UserId;
        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        ServiceResult<MachineAuthorizedKey> result = await _authorizedKeyService.AuthorizeKeyAsync(
            machineId, req.SigningKeyId, userId.Value, tenantId, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Machine or signing key not found", ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            await HttpContext.SendApiErrorAsync(result.StatusCode, result.ErrorMessage ?? "Authorization failed", ct);

            return;
        }

        if (result.Data is null)
        {
            await HttpContext.SendApiErrorAsync(500, "Unexpected null result", ct);

            return;
        }

        // Re-fetch the full list for this machine to get joined display data for the response.
        ServiceResult<List<MachineAuthorizedKeyDto>> listResult = await _authorizedKeyService.ListAuthorizedKeysAsync(
            machineId, tenantId, ct);

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
