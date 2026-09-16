// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Which protection degraded when Redis was unavailable.
/// </summary>
public enum ResilienceComponent
{
    /// <summary>Rate limiting admitted a request it could not count.</summary>
    RateLimiter,

    /// <summary>Telemetry deduplication admitted a batch it could not check. The database unique
    /// index remains the backstop, so duplicates are still dropped at insert time.</summary>
    TelemetryDedup,

    /// <summary>The failed-SSH-login wake-up gate could not tell whether a window was already open,
    /// so an evaluation was scheduled without one. Alerting stays alive and the count still comes
    /// from telemetry rows; what degrades is the suppression of redundant evaluations. It is a
    /// separate member from <see cref="RateLimiter"/> because the two fail open for different
    /// reasons and an operator needs to tell them apart.</summary>
    SshFailureWindow,
}
