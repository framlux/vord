// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// A stand-in gauge class that cannot be constructed, standing for a real one whose backing
/// configuration or service is missing.
/// </summary>
public sealed class ThrowingObservableMetrics : IObservableMetrics
{
    /// <summary>
    /// Always fails, so the startup pass has to decide what a gauge it cannot build costs.
    /// </summary>
    public ThrowingObservableMetrics()
    {
        throw new InvalidOperationException("the backing configuration is missing");
    }
}
