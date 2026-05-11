// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Alerts;

/// <summary>
/// Evaluates event-based alert rules (e.g., SSH connections) at telemetry ingestion time
/// rather than in the periodic evaluation loop.
/// </summary>
public interface IEventAlertService
{
    /// <summary>
    /// Evaluates SSH connection alert rules for a machine when a new SSH session is detected.
    /// Creates an alert event and enqueues delivery if a matching rule exists and no active event is pending.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="user">The SSH user name.</param>
    /// <param name="sourceIp">The source IP address of the SSH connection.</param>
    /// <param name="sourcePort">The source port of the SSH connection.</param>
    /// <param name="authMethod">The authentication method used.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EvaluateSshConnectAsync(int tenantId, long machineId, string user, string sourceIp, int sourcePort, string authMethod, CancellationToken ct);

    /// <summary>
    /// Auto-resolves active SSH connection alert events for a machine when a disconnect is received.
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ResolveSshDisconnectAsync(long machineId, CancellationToken ct);
}
