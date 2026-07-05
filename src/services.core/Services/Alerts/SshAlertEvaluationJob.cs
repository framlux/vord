// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Out-of-band evaluation of a single SSH session event. Enqueued from the telemetry ingest path
/// so the gRPC ack returns immediately rather than blocking on per-item alert evaluation. One
/// enqueue per SSH item keeps each job small and independently retryable.
/// </summary>
public sealed class SshAlertEvaluationJob
{
    /// <summary>The SSH action that opens a session and is evaluated for connect alerts.</summary>
    public const string ConnectAction = "connect";

    /// <summary>The SSH action that closes a session and resolves an open connect alert.</summary>
    public const string DisconnectAction = "disconnect";

    /// <summary>
    /// The SSH actions this job actually acts on. Any other action (e.g. <c>failed</c>, emitted for
    /// every failed auth attempt) is a no-op, so the ingest path must not enqueue a job for it. This is
    /// the single source of truth shared by the enqueue filter and the job's own guard.
    /// </summary>
    public static readonly IReadOnlySet<string> EvaluatedActions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ConnectAction, DisconnectAction };

    /// <summary>Returns true when the given SSH action produces alert work worth enqueuing.</summary>
    /// <param name="action">The SSH session action.</param>
    /// <returns><c>true</c> when the action is evaluated; otherwise <c>false</c>.</returns>
    public static bool IsEvaluatedAction(string action) => EvaluatedActions.Contains(action);

    private readonly IEventAlertService _eventAlertService;
    private readonly ILogger<SshAlertEvaluationJob> _logger;

    /// <summary>Creates a new instance of the <see cref="SshAlertEvaluationJob"/> class.</summary>
    public SshAlertEvaluationJob(IEventAlertService eventAlertService, ILogger<SshAlertEvaluationJob> logger)
    {
        ArgumentNullException.ThrowIfNull(eventAlertService);
        ArgumentNullException.ThrowIfNull(logger);

        _eventAlertService = eventAlertService;
        _logger = logger;
    }

    /// <summary>Evaluates one SSH session event for the given machine.</summary>
    [Queue("critical")]
    public async Task RunAsync(
        int tenantId, long machineId, string action,
        string user, string sourceIp, int sourcePort, string authMethod,
        CancellationToken ct)
    {
        if (string.Equals(action, ConnectAction, StringComparison.OrdinalIgnoreCase))
        {
            await _eventAlertService.EvaluateSshConnectAsync(tenantId, machineId, user, sourceIp, sourcePort, authMethod, ct);
        }
        else if (string.Equals(action, DisconnectAction, StringComparison.OrdinalIgnoreCase))
        {
            await _eventAlertService.ResolveSshDisconnectAsync(machineId, ct);
        }
        else
        {
            _logger.LogDebug("Ignoring SSH action {Action} for machine {MachineId}", action, machineId);
        }
    }
}
