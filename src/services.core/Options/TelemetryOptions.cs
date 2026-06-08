// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Configuration for the telemetry submission service. Surfaces the long-running stream
/// limits and the subscription-recheck cadence so operators can tune resource ceilings
/// without code changes.
/// </summary>
public sealed class TelemetryOptions
{
    /// <summary>
    /// Maximum duration (minutes) for a single telemetry stream before the server closes it.
    /// Default 5.
    /// </summary>
    [Range(1, 60)]
    public int MaxStreamDurationMinutes { get; set; } = 5;

    /// <summary>
    /// Maximum number of envelopes a single stream may submit before the server closes it.
    /// Default 1000.
    /// </summary>
    [Range(1, 100_000)]
    public int MaxEnvelopesPerStream { get; set; } = 1000;

    /// <summary>
    /// Maximum number of concurrent telemetry streams allowed per machine identity. Default 1.
    /// A higher cap permits brief overlap during reconnection but does not protect against a
    /// misbehaving agent holding many simultaneous streams.
    /// </summary>
    [Range(1, 100)]
    public int MaxConcurrentStreamsPerMachine { get; set; } = 1;

    /// <summary>
    /// Cadence (seconds) for re-checking the tenant's subscription status inside a long-running
    /// stream. Default 30. Streams continue to ingest until the next interval check; choose
    /// a value that balances enforcement responsiveness against DB load.
    /// </summary>
    [Range(5, 300)]
    public int SubscriptionRecheckIntervalSeconds { get; set; } = 30;
}
