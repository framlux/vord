// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// A stand-in gauge class whose only job is to note that the container constructed it, which is
/// where a real gauge would create its observable instrument.
/// </summary>
public sealed class CountingObservableMetrics : IObservableMetrics
{
    /// <summary>
    /// Creates the stand-in and notes the construction.
    /// </summary>
    /// <param name="log">The shared record of what was constructed.</param>
    public CountingObservableMetrics(MetricsConstructionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.Record(nameof(CountingObservableMetrics));
    }
}
