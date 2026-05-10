// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Commands;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Commands;

/// <summary>
/// Lists command history for a machine.
/// </summary>
public sealed class CommandListEndpoint : EndpointWithoutRequest<ApiResponse<List<CommandDto>>>
{
    private readonly IRemoteCommandService _commandService;

    /// <summary>
    /// Creates a new instance of the <see cref="CommandListEndpoint"/> class.
    /// </summary>
    public CommandListEndpoint(IRemoteCommandService commandService)
    {
        _commandService = commandService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/{id}/commands");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long machineId = Route<long>("id");
        int page = Math.Max(1, Query<int?>("page", isRequired: false) ?? 1);
        int pageSize = Math.Clamp(Query<int?>("pageSize", isRequired: false) ?? 25, 1, 100);

        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<List<CommandDto>>.Error("Unable to identify tenant"), ct);

            return;
        }

        List<RemoteCommand> commands = await _commandService.GetCommandHistoryAsync(machineId, tenantId.Value, page, pageSize, ct);

        List<CommandDto> dtos = commands.Select(c => new CommandDto
        {
            Id = c.Id,
            CommandId = c.CommandId,
            MachineId = c.MachineId,
            CommandType = c.CommandType,
            Status = c.Status.ToString(),
            CreatedAt = c.CreatedAt,
            ExpiresAt = c.ExpiresAt,
            DeliveredAt = c.DeliveredAt,
            CompletedAt = c.CompletedAt,
            ExitCode = c.ExitCode,
            ResultMessage = c.ResultMessage,
        }).ToList();

        await Send.OkAsync(ApiResponse<List<CommandDto>>.Ok(dtos), cancellation: ct);
    }
}
