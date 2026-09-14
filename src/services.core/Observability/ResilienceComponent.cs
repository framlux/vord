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
}
