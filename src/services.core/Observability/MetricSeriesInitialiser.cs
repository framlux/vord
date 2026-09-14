// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Records a zero on every closed-enum series at startup, so a failure that happens once is still
/// visible to a rule watching for an increase.
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
    private readonly ILogger<MetricSeriesInitialiser> _logger;

    /// <summary>
    /// Creates the initialiser.
    /// </summary>
    /// <param name="metrics">Every metric class registered in this process that pre-records series.</param>
    /// <param name="logger">The logger.</param>
    public MetricSeriesInitialiser(
        IEnumerable<IInitialisableMetrics> metrics,
        ILogger<MetricSeriesInitialiser> logger)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
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
