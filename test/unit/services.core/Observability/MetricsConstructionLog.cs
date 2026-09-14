// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Records which stand-in metric classes the container actually constructed, so a test can tell
/// construction by the initialiser apart from construction by the test itself.
/// </summary>
public sealed class MetricsConstructionLog
{
    private readonly List<string> _constructed = [];

    /// <summary>The class names constructed so far, in order.</summary>
    public IReadOnlyList<string> Constructed => _constructed;

    /// <summary>
    /// Notes that a metric class was constructed.
    /// </summary>
    /// <param name="metricClass">The name of the class.</param>
    public void Record(string metricClass)
    {
        _constructed.Add(metricClass);
    }
}
