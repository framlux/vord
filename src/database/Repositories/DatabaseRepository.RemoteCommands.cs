// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Linq;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Database cache operations for remote commands.
/// </summary>
public partial class DatabaseRepository : IRemoteCommandRepository
{
    /// <inheritdoc/>
    public async Task<RemoteCommand> CreateRemoteCommandAsync(RemoteCommand command, CancellationToken cancellationToken = default)
    {
        long id = await _db.InsertWithInt64IdentityAsync(command, token: cancellationToken);
        command.Id = id;

        return command;
    }

    /// <inheritdoc/>
    public async Task<List<RemoteCommand>> GetPendingCommandsForMachineAsync(long machineId, int tenantId, CancellationToken cancellationToken = default)
    {
        return await _db.RemoteCommands
            .Where(c => c.MachineId == machineId &&
                        c.TenantId == tenantId &&
                        c.Status == RemoteCommandStatus.Pending &&
                        c.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<RemoteCommand?> GetRemoteCommandByCommandIdAsync(string commandId, CancellationToken cancellationToken = default)
    {
        return await _db.RemoteCommands
            .FirstOrDefaultAsync(c => c.CommandId == commandId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<RemoteCommand?> GetRemoteCommandByIdAsync(long id, int tenantId, CancellationToken cancellationToken = default)
    {
        return await _db.RemoteCommands
            .LoadWith(c => c.User)
            .LoadWith(c => c.Machine)
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<RemoteCommand>> GetCommandsForMachineAsync(long machineId, int tenantId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        return await _db.RemoteCommands
            .Where(c => c.MachineId == machineId && c.TenantId == tenantId)
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateRemoteCommandStatusAsync(string commandId, long machineId, RemoteCommandStatus status, int? exitCode, string? stdout, string? stderr, string? resultMessage, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string? truncatedStdout = stdout?.Length > 2064 ? stdout[..2064] : stdout;
        string? truncatedStderr = stderr?.Length > 2064 ? stderr[..2064] : stderr;

        IUpdatable<RemoteCommand> update = _db.RemoteCommands
            .Where(c => c.CommandId == commandId && c.MachineId == machineId)
            .Set(c => c.Status, status);

        if (status == RemoteCommandStatus.Delivered)
        {
            update = update.Set(c => c.DeliveredAt, now);
        }

        if (status == RemoteCommandStatus.Executed || status == RemoteCommandStatus.Failed || status == RemoteCommandStatus.Rejected)
        {
            update = update
                .Set(c => c.CompletedAt, now)
                .Set(c => c.ExitCode, exitCode)
                .Set(c => c.Stdout, truncatedStdout)
                .Set(c => c.Stderr, truncatedStderr)
                .Set(c => c.ResultMessage, resultMessage);
        }

        await update.UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> IsNonceUsedAsync(string nonce, CancellationToken cancellationToken = default)
    {
        return await _db.RemoteCommands
            .AnyAsync(c => c.Nonce == nonce, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ExpirePendingCommandsAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _db.RemoteCommands
            .Where(c => c.Status == RemoteCommandStatus.Pending && c.ExpiresAt <= now)
            .Set(c => c.Status, RemoteCommandStatus.Expired)
            .Set(c => c.CompletedAt, now)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task MarkCommandsDeliveredAsync(IEnumerable<string> commandIds, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _db.RemoteCommands
            .Where(c => commandIds.Contains(c.CommandId) && c.Status == RemoteCommandStatus.Pending)
            .Set(c => c.Status, RemoteCommandStatus.Delivered)
            .Set(c => c.DeliveredAt, now)
            .UpdateAsync(cancellationToken);
    }
}
