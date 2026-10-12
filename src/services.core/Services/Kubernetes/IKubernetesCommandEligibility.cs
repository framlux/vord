// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Decides whether a machine may be sent one of the cluster reporter commands. Called for every remote
/// command once the machine is loaded and the signing key is authorized for it, so the commands that are
/// not Kubernetes commands pass through untouched.
/// </summary>
public interface IKubernetesCommandEligibility
{
    /// <summary>
    /// Checks a command against the target machine's latest local Kubernetes report and reporter state.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the machine and the command.</param>
    /// <param name="machine">The target machine, already loaded for the tenant.</param>
    /// <param name="commandType">The command type exactly as submitted.</param>
    /// <param name="commandParams">The JSON-encoded command parameters as submitted, or null.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A success result, or a 400 result whose message tells the caller why the command is refused.</returns>
    Task<ServiceResult<bool>> CheckAsync(int tenantId, Machine machine, string commandType, string? commandParams, CancellationToken ct);
}
