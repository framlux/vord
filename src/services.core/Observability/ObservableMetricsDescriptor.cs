// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Names a gauge-backed metric class that must be constructed at startup, without constructing it.
/// </summary>
/// <remarks>
/// The registration describes the class rather than resolving it because the container materialises
/// an injected collection before the injecting type exists: a gauge whose constructor failed would
/// take the whole process down with it. Handing the initialiser a type to resolve itself is what
/// turns that into one gauge reporting nothing, which is the cost the design accepts.
/// </remarks>
public sealed class ObservableMetricsDescriptor
{
    private ObservableMetricsDescriptor(Type metricsType)
    {
        MetricsType = metricsType;
    }

    /// <summary>
    /// The metric class to resolve from the container at startup.
    /// </summary>
    public Type MetricsType { get; }

    /// <summary>
    /// Describes a gauge-backed metric class.
    /// </summary>
    /// <typeparam name="TMetrics">The metric class, registered in its own right elsewhere.</typeparam>
    /// <returns>The descriptor to register alongside it.</returns>
    public static ObservableMetricsDescriptor For<TMetrics>()
        where TMetrics : class, IObservableMetrics
    {
        return new ObservableMetricsDescriptor(typeof(TMetrics));
    }
}
