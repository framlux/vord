// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Records a zero on every closed-enum series at startup, so a failure that happens once is still
/// visible to a rule watching for an increase, and forces every gauge class into existence so its
/// series exist at all.
/// </summary>
/// <remarks>
/// This runs as a hosted service rather than during registration because instruments only exist on
/// a constructed metric class, and registration deals in descriptions of services that do not exist
/// yet. Losing a class's series costs first-failure detection, not the correctness of the process,
/// so both halves are logged and swallowed rather than allowed to stop startup. That is also why
/// the gauge classes arrive as descriptors and are resolved here: injecting them would put their
/// construction ahead of this method, where a failure has no owner and kills the host instead.
/// </remarks>
public sealed class MetricSeriesInitialiser : IHostedService
{
    private readonly IEnumerable<IInitialisableMetrics> _metrics;
    private readonly IEnumerable<ObservableMetricsDescriptor> _observableMetrics;
    private readonly IServiceProvider _services;
    private readonly ILogger<MetricSeriesInitialiser> _logger;

    /// <summary>
    /// Creates the initialiser.
    /// </summary>
    /// <param name="metrics">Every metric class registered in this process that pre-records series.</param>
    /// <param name="observableMetrics">A descriptor for every gauge-backed metric class registered
    /// in this process.</param>
    /// <param name="services">The container the gauge classes are resolved from.</param>
    /// <param name="logger">The logger.</param>
    public MetricSeriesInitialiser(
        IEnumerable<IInitialisableMetrics> metrics,
        IEnumerable<ObservableMetricsDescriptor> observableMetrics,
        IServiceProvider services,
        ILogger<MetricSeriesInitialiser> logger)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(observableMetrics);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _metrics = metrics;
        _observableMetrics = observableMetrics;
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (ObservableMetricsDescriptor descriptor in _observableMetrics)
        {
            try
            {
                // Resolving the class is the entire point: constructing it is what creates its
                // observable instruments, and a gauge nothing else injects would otherwise never
                // exist. There is nothing to call on it afterwards.
                _ = _services.GetRequiredService(descriptor.MetricsType);

                _logger.LogDebug("Registered observable metrics {MetricClass}", descriptor.MetricsType.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to construct observable metrics {MetricClass}; the series it reports will not exist",
                    descriptor.MetricsType.Name);
            }
        }

        foreach (IInitialisableMetrics metrics in _metrics)
        {
            try
            {
                metrics.InitialiseSeries();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to pre-record metric series for {MetricClass}; a first failure on those series may go unnoticed",
                    metrics.GetType().Name);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
