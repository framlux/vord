// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Admin;

/// <summary>
/// Shared preamble logic for the fleet's own administration endpoints.
/// </summary>
internal static class AdminEndpointGuards
{
    /// <summary>
    /// Writes a not-found response and returns true when this deployment has no fleet-local
    /// administration console. The hosted product is administered from the internal operator
    /// application, so the surface does not exist here rather than being merely off limits.
    /// </summary>
    internal static async Task<bool> RefusedForDeploymentAsync(
        HttpContext httpContext,
        DeploymentMode deploymentMode,
        CancellationToken ct)
    {
        if (deploymentMode.IsSaas == false)
        {
            return false;
        }

        await httpContext.SendApiErrorAsync(404, "Endpoint not available when billing is enabled", ct);

        return true;
    }
}
