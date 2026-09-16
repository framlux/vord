// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Alerts;

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

    /// <summary>
    /// Evaluates every enabled failed-SSH-login rule for a machine over that rule's own trailing
    /// window, ending at the moment of the call. Rules whose window is over threshold raise an
    /// alert event; rules whose window is empty have their events resolved.
    /// </summary>
    /// <remarks>
    /// The window ends at now rather than at a fixed past bucket, so a job that runs late never
    /// resolves through failures that arrived after a boundary. It starts at the rule's own duration
    /// before that, widened back to <paramref name="windowOpenedAt"/> when the job ran later than it
    /// was scheduled for — otherwise the telemetry that opened the window would sit just outside it
    /// and a burst delivered in one envelope would never be counted at all. Widening only ever adds
    /// rows, so it cannot make a resolve less conservative.
    /// <para>
    /// The count comes from telemetry rows, never from the ingest gate, which is why a rule with a
    /// ten-minute window and a built-in with a five-minute one can both be answered by one call.
    /// </para>
    /// </remarks>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="windowOpenedAt">
    /// When this evaluation's window was opened — the ingest instant for the first run of a chain,
    /// and the previous run's instant for every run after it, so consecutive windows leave no gap.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> when at least one rule for this machine is still in an unresolved incident, so
    /// the window must be re-checked; <c>false</c> when everything is quiet and the chain can end.
    /// </returns>
    Task<bool> EvaluateFailedSshLoginWindowAsync(int tenantId, long machineId, DateTimeOffset windowOpenedAt, CancellationToken ct);
}
