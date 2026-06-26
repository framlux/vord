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
        if (string.Equals(action, "connect", StringComparison.OrdinalIgnoreCase))
        {
            await _eventAlertService.EvaluateSshConnectAsync(tenantId, machineId, user, sourceIp, sourcePort, authMethod, ct);
        }
        else if (string.Equals(action, "disconnect", StringComparison.OrdinalIgnoreCase))
        {
            await _eventAlertService.ResolveSshDisconnectAsync(machineId, ct);
        }
        else
        {
            _logger.LogDebug("Ignoring SSH action {Action} for machine {MachineId}", action, machineId);
        }
    }
}
