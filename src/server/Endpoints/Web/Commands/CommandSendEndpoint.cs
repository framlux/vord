// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Commands;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Kubernetes;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Commands;

/// <summary>
/// Submits a signed remote command to a machine.
/// </summary>
/// <remarks>
/// Remote commands are a Team feature, except the three cluster reporter commands, which every tier may
/// send. Which applies depends on the command type in the request body, so the check is made here, after
/// request validation, and not by the declarative Team tag, which can only judge the endpoint as a whole.
/// The service then requires the signed payload to name the same type, so the type judged here is the one
/// the agent runs.
/// </remarks>
public sealed class CommandSendEndpoint : Endpoint<CommandSendRequest, ApiResponse<CommandDto>>
{
    /// <summary>
    /// The message returned with the 403 for a command that needs a Team subscription.
    /// </summary>
    internal const string RequiresTeamMessage = "Remote commands require a Team subscription";

    private readonly RemoteCommandService _commandService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="CommandSendEndpoint"/> class.
    /// </summary>
    public CommandSendEndpoint(RemoteCommandService commandService, ISubscriptionService subscriptionService, ITenantContext tenantContext)
    {
        _commandService = commandService;
        _subscriptionService = subscriptionService;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/commands");
        Policies(AuthorizationPolicies.MachineAdmin);
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CommandSendRequest req, CancellationToken ct)
    {
        int tenantId = _tenantContext.RequireTenantId();

        int? userId = _tenantContext.UserId;
        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        // Resolved the way the Team pre-processor resolves it, so a self-hosted deployment, whose
        // subscription service answers with a Team-level row for every tenant, passes here as it does there.
        if (KubernetesCommandTypes.IsReporterCommand(req.CommandType) == false)
        {
            TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
            if (SubscriptionPolicy.RequiresTeam(subscription))
            {
                await HttpContext.SendApiErrorAsync(403, RequiresTeamMessage, ct);

                return;
            }
        }

        RemoteCommand command = new()
        {
            CommandId = req.CommandId,
            TenantId = tenantId,
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
            await HttpContext.SendApiErrorAsync(result.StatusCode, result.ErrorMessage ?? "Command submission failed", ct);

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
