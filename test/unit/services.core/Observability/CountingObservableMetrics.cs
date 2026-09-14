// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// A stand-in gauge class whose only job is to record that it was constructed at all.
/// </summary>
public sealed class CountingObservableMetrics : IObservableMetrics
{
    /// <summary>
    /// Always true on a constructed instance. Reading it from the initialiser's enumeration proves
    /// the marker was resolved, which is the entire mechanism under test.
    /// </summary>
    public bool WasResolved { get; } = true;
}
