// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

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
/// yet. Failing to pre-record costs first-failure detection, not the correctness of the process, so
/// it is logged and swallowed rather than allowed to stop startup.
/// </remarks>
public sealed class MetricSeriesInitialiser : IHostedService
{
    private readonly IEnumerable<IInitialisableMetrics> _metrics;
    private readonly IEnumerable<IObservableMetrics> _observableMetrics;
    private readonly ILogger<MetricSeriesInitialiser> _logger;

    /// <summary>
    /// Creates the initialiser.
    /// </summary>
    /// <param name="metrics">Every metric class registered in this process that pre-records series.</param>
    /// <param name="observableMetrics">Every gauge-backed metric class registered in this process.</param>
    /// <param name="logger">The logger.</param>
    public MetricSeriesInitialiser(
        IEnumerable<IInitialisableMetrics> metrics,
        IEnumerable<IObservableMetrics> observableMetrics,
        ILogger<MetricSeriesInitialiser> logger)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(observableMetrics);
        ArgumentNullException.ThrowIfNull(logger);
        _metrics = metrics;
        _observableMetrics = observableMetrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolving each gauge class is the entire point: constructing it is what creates its
        // observable instruments, and a gauge nothing else injects would otherwise never exist.
        // There is nothing to call on them afterwards.
        foreach (IObservableMetrics observable in _observableMetrics)
        {
            try
            {
                _logger.LogDebug("Registered observable metrics {MetricClass}", observable.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to construct observable metrics; the series it reports will not exist");
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
