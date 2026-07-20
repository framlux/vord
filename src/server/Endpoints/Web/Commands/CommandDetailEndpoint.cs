// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Commands;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Commands;

/// <summary>
/// Gets detail for a specific remote command.
/// </summary>
public sealed class CommandDetailEndpoint : EndpointWithoutRequest<ApiResponse<CommandDto>>
{
    private readonly RemoteCommandService _commandService;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="CommandDetailEndpoint"/> class.
    /// </summary>
    public CommandDetailEndpoint(RemoteCommandService commandService, ITenantContext tenantContext)
    {
        _commandService = commandService;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/commands/{id}");
        Policies("ViewOnly");
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long commandId = Route<long>("id");

        int tenantId = _tenantContext.RequireTenantId();

        ServiceResult<RemoteCommand> result = await _commandService.GetCommandDetailAsync(commandId, tenantId, ct);
        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Command not found", ct);

            return;
        }

        RemoteCommand cmd = result.Data!;

        await Send.OkAsync(ApiResponse<CommandDto>.Ok(new CommandDto
        {
            Id = cmd.Id,
            CommandId = cmd.CommandId,
            MachineId = cmd.MachineId,
            CommandType = cmd.CommandType,
            Status = cmd.Status.ToString(),
            CreatedAt = cmd.CreatedAt,
            ExpiresAt = cmd.ExpiresAt,
            DeliveredAt = cmd.DeliveredAt,
            CompletedAt = cmd.CompletedAt,
            ExitCode = cmd.ExitCode,
            ResultMessage = cmd.ResultMessage,
        }), cancellation: ct);
    }
}
