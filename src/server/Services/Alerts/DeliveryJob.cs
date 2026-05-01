// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Alerts;

/// <summary>
/// Represents a serialized delivery job from the Redis queue.
/// </summary>
internal sealed class DeliveryJob
{
    /// <summary>The alert event identifier.</summary>
    public long EventId { get; set; }

    /// <summary>The alert rule identifier.</summary>
    public int RuleId { get; set; }

    /// <summary>The tenant identifier.</summary>
    public int TenantId { get; set; }

    /// <summary>The number of retry attempts made so far.</summary>
    public int RetryCount { get; set; }

    /// <summary>Earliest time this job should be processed. Null means immediately.</summary>
    public DateTimeOffset? NotBefore { get; set; }
}
