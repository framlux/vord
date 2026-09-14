// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Reports how many machines the installation has, by health status, across every tenant.
/// </summary>
/// <remarks>
/// Hosted in the worker deliberately. The ingest-stalled rule is gated on this series so it cannot
/// page forever in a deployment with no machines; were the gauge hosted in the API server, that
/// process dying would remove both the ingest series and the gate together, and the rule written for
/// that outage could not fire during it.
///
/// A measurement is emitted for every status, including the ones at zero. Iterating the query result
/// instead would mean a status with no machines produced no series at all, which is the single
/// mistake that would quietly break the gate.
/// </remarks>
public sealed class FleetMetrics : IObservableMetrics
{
    /// <summary>
    /// How long the measurement may take before it is abandoned. The callback runs on the exporter's
    /// collection thread, so a hung query without this would stall every instrument in the process.
    /// </summary>
    private static readonly TimeSpan MeasurementTimeout = TimeSpan.FromSeconds(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FleetMetrics> _logger;

    /// <summary>
    /// Creates the fleet instrument.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    /// <param name="scopeFactory">Used to resolve a scoped repository per observation.</param>
    /// <param name="logger">The logger.</param>
    public FleetMetrics(
        IMeterFactory meterFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<FleetMetrics> logger)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;

        Meter meter = meterFactory.Create(VordMeter.Name);

        meter.CreateObservableGauge(
            "vord.fleet.machines",
            ObserveMachines,
            description: "Machines across every tenant, by health status.");
    }

    private IEnumerable<Measurement<long>> ObserveMachines()
    {
        try
        {
            using CancellationTokenSource timeout = new(MeasurementTimeout);
            using IServiceScope scope = _scopeFactory.CreateScope();
            IMachineStateRepository repository = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

            IReadOnlyDictionary<short, int> counts = repository
                .GetFleetMachineCountsByHealthAsync(timeout.Token)
                .GetAwaiter().GetResult();

            List<Measurement<long>> measurements = [];

            // Iterating the enum, never the dictionary: a status with no machines must still report
            // zero, or the series the ingest rule gates on would not exist.
            foreach (MachineHealthStatus status in Enum.GetValues<MachineHealthStatus>())
            {
                measurements.Add(new Measurement<long>(
                    counts.GetValueOrDefault((short)status),
                    new KeyValuePair<string, object?>("status", MetricTag.From(status))));
            }

            return measurements;
        }
        catch (Exception ex)
        {
            // No measurement rather than a throw: an exception here aborts the whole collection
            // cycle, so one unhealthy query would blind every other instrument in the process.
            _logger.LogWarning(ex, "Could not count fleet machines by health status");

            return [];
        }
    }
}
