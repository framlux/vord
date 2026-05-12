// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Service for managing per-machine signing key authorizations.
/// </summary>
public interface IMachineAuthorizedKeyService
{
    /// <summary>
    /// Authorizes a signing key for a specific machine, allowing remote commands to be issued.
    /// </summary>
    /// <param name="machineId">The machine to authorize the key for</param>
    /// <param name="signingKeyId">The signing key to authorize</param>
    /// <param name="userId">The user performing the authorization</param>
    /// <param name="tenantId">The tenant that owns the machine and signing key</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a service result with the created authorization record</returns>
    Task<ServiceResult<MachineAuthorizedKey>> AuthorizeKeyAsync(long machineId, int signingKeyId, int userId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a signing key authorization for a specific machine.
    /// </summary>
    /// <param name="machineId">The machine to revoke the key authorization from</param>
    /// <param name="signingKeyId">The signing key whose authorization to revoke</param>
    /// <param name="userId">The user performing the revocation</param>
    /// <param name="tenantId">The tenant that owns the machine and signing key</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a service result indicating success or failure</returns>
    Task<ServiceResult<bool>> RevokeAuthorizationAsync(long machineId, int signingKeyId, int userId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all authorized keys for a machine, including revoked authorizations.
    /// </summary>
    /// <param name="machineId">The machine to list authorized keys for</param>
    /// <param name="tenantId">The tenant that owns the machine</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a service result with the list of authorized key DTOs</returns>
    Task<ServiceResult<List<MachineAuthorizedKeyDto>>> ListAuthorizedKeysAsync(long machineId, int tenantId, CancellationToken cancellationToken = default);
}
