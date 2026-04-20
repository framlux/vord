// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Commands;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Commands;

/// <summary>
/// Submits a signed remote command to a machine.
/// </summary>
public sealed class CommandSendEndpoint : Endpoint<CommandSendRequest, ApiResponse<CommandDto>>
{
    private readonly IRemoteCommandService _commandService;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="CommandSendEndpoint"/> class.
    /// </summary>
    public CommandSendEndpoint(IRemoteCommandService commandService, ISubscriptionService subscriptionService)
    {
        _commandService = commandService;
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/commands");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CommandSendRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier != SubscriptionTier.Team))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<CommandDto>.Error("Remote commands require a Team subscription"), ct);

            return;
        }

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<CommandDto>.Error("Unable to identify user"), ct);

            return;
        }

        RemoteCommand command = new()
        {
            CommandId = req.CommandId,
            TenantId = tenantId.Value,
            MachineId = req.MachineId,
            UserId = userId.Value,
            SigningKeyId = req.SigningKeyId,
            CommandType = req.CommandType,
            Params = req.Params,
            Nonce = req.Nonce,
            Signature = req.Signature,
            CanonicalPayload = req.CanonicalPayload,
            Timestamp = req.Timestamp,
            ExpiresAt = req.ExpiresAt,
            Status = Database.Enums.RemoteCommandStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        ServiceResult<RemoteCommand> result = await _commandService.SubmitCommandAsync(command, ct);

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<CommandDto>.Error("Command submission failed"), ct);

            return;
        }

        RemoteCommand created = result.Data!;

        await Send.OkAsync(ApiResponse<CommandDto>.Ok(new CommandDto
        {
            Id = created.Id,
            CommandId = created.CommandId,
            MachineId = created.MachineId,
            CommandType = created.CommandType,
            Status = created.Status.ToString(),
            CreatedAt = created.CreatedAt,
            ExpiresAt = created.ExpiresAt,
        }), cancellation: ct);
    }
}
