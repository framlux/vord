// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for remote command operations.
/// </summary>
public interface IRemoteCommandRepository
{
    /// <summary>
    /// Creates a new remote command in the database.
    /// </summary>
    Task<RemoteCommand> CreateRemoteCommandAsync(RemoteCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets pending commands for a specific machine that have not expired.
    /// </summary>
    Task<List<RemoteCommand>> GetPendingCommandsForMachineAsync(long machineId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a remote command by its client-generated command ID.
    /// </summary>
    Task<RemoteCommand?> GetRemoteCommandByCommandIdAsync(string commandId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a remote command by its database ID with related entities.
    /// </summary>
    Task<RemoteCommand?> GetRemoteCommandByIdAsync(long id, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets command history for a machine with pagination.
    /// </summary>
    Task<List<RemoteCommand>> GetCommandsForMachineAsync(long machineId, int tenantId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a remote command's status and result fields.
    /// </summary>
    Task UpdateRemoteCommandStatusAsync(string commandId, long machineId, RemoteCommandStatus status, int? exitCode, string? stdout, string? stderr, string? resultMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a nonce has already been used by any remote command.
    /// </summary>
    Task<bool> IsNonceUsedAsync(string nonce, CancellationToken cancellationToken = default);

    /// <summary>
    /// Expires all pending commands that have passed their expiry time.
    /// </summary>
    Task ExpirePendingCommandsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks commands as delivered after they have been sent to the agent via gRPC.
    /// </summary>
    Task MarkCommandsDeliveredAsync(IEnumerable<string> commandIds, CancellationToken cancellationToken = default);
}
