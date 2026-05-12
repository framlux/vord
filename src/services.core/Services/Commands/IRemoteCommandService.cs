// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Commands;

/// <summary>
/// Service for managing remote commands sent to machines.
/// </summary>
public interface IRemoteCommandService
{
    /// <summary>
    /// Submits a signed remote command. The server verifies the Ed25519 signature, command type, machine ownership, and nonce uniqueness.
    /// </summary>
    /// <param name="command">The remote command to submit</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns the created command, or an error result</returns>
    Task<ServiceResult<RemoteCommand>> SubmitCommandAsync(RemoteCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets command history for a machine.
    /// </summary>
    /// <param name="machineId">The machine ID</param>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Page size</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a list of remote commands</returns>
    Task<List<RemoteCommand>> GetCommandHistoryAsync(long machineId, int tenantId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single command by its database ID.
    /// </summary>
    /// <param name="id">The command database ID</param>
    /// <param name="tenantId">The tenant ID for authorization</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns the command if found</returns>
    Task<ServiceResult<RemoteCommand>> GetCommandDetailAsync(long id, int tenantId, CancellationToken cancellationToken = default);
}
