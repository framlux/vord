// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Derives the rate-limit partition key for a gRPC call. Prefers the authenticated machine claim,
/// then the tenant claim, and only falls back to the peer IP when the call is unauthenticated.
/// Keying on identity rather than IP prevents all agents sharing the ingress's few source IPs from
/// either sharing one bucket (self-DoS) or making the limiter a no-op.
/// </summary>
internal static class GrpcRateLimitPartitionKey
{
    /// <summary>
    /// Returns the partition key for the call: <c>machine:{id}</c>, else <c>tenant:{id}</c>,
    /// else <c>ip:{peerIp}</c> for unauthenticated callers.
    /// </summary>
    /// <param name="user">The authenticated principal, or null when unauthenticated.</param>
    /// <param name="peerIp">The peer IP used as the pre-auth fallback.</param>
    internal static string Derive(ClaimsPrincipal? user, string peerIp)
    {
        string? machineId = user?.FindFirst("MachineId")?.Value;
        if (string.IsNullOrEmpty(machineId) == false)
        {
            return "machine:" + machineId;
        }

        string? tenantId = user?.FindFirst("TenantId")?.Value;
        if (string.IsNullOrEmpty(tenantId) == false)
        {
            return "tenant:" + tenantId;
        }

        return "ip:" + peerIp;
    }
}
