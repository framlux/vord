// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Reports how far behind the Kubernetes inventory projector is, in seconds.
/// </summary>
/// <remarks>
/// Lag is the age of the oldest telemetry row the projector has not yet applied and would actually read,
/// so a projector with nothing to do reports zero rather than the ever-growing age of whatever it last
/// handled. The cursor itself is a row id and cannot answer this, which is why a dedicated query exists,
/// and why that query repeats the projector's read exactly: a row the projector will never read is not
/// lag. The projector is one stream under one lock, so the gauge has no tags. Its cursor is shared by
/// every replica, which means every worker reports the same figure: aggregate with the maximum, never
/// the sum.
/// </remarks>
public sealed class KubernetesProjectionMetrics : IObservableMetrics
{
    /// <summary>
    /// How long a measurement may take before it is abandoned, counted on the injected clock so a test
    /// can pass it without waiting. The callback runs on the exporter's collection thread, so a hung
    /// query without this would stall every instrument in the process, not just this one.
    /// </summary>
    private static readonly TimeSpan MeasurementTimeout = TimeSpan.FromSeconds(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KubernetesProjectionMetrics> _logger;
    private readonly TimeSpan _visibilityLag;

    /// <summary>
    /// Creates the Kubernetes projection instrument.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    /// <param name="scopeFactory">Used to resolve a scoped repository per observation.</param>
    /// <param name="timeProvider">The clock lag is measured against.</param>
    /// <param name="streamingOptions">Streaming options carrying the visibility lag the projector applies.</param>
    /// <param name="logger">The logger.</param>
    public KubernetesProjectionMetrics(
        IMeterFactory meterFactory,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<StreamingOptions> streamingOptions,
        ILogger<KubernetesProjectionMetrics> logger)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(streamingOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;

        // Clamped the way the projector clamps it, so the gauge measures the row set the projector
        // actually reads even under a misconfigured lag.
        _visibilityLag = TimeSpan.FromSeconds(Math.Max(0, streamingOptions.Value.VisibilityLagSeconds));

        Meter meter = meterFactory.Create(VordMeter.Name);

        meter.CreateObservableGauge(
            "vord.kubernetes.projection.lag_seconds",
            ObserveLag,
            unit: "s",
            description: "Age of the oldest Kubernetes telemetry row the inventory projector has not yet applied.");
    }

    private IEnumerable<Measurement<double>> ObserveLag()
    {
        double? lag = MeasureLag();
        if (lag is null)
        {
            return [];
        }

        return [new Measurement<double>(lag.Value)];
    }

    private double? MeasureLag()
    {
        try
        {
            using CancellationTokenSource timeout = new(MeasurementTimeout, _timeProvider);
            using IServiceScope scope = _scopeFactory.CreateScope();
            IKubernetesInventoryRepository repository = scope.ServiceProvider.GetRequiredService<IKubernetesInventoryRepository>();

            DateTimeOffset now = _timeProvider.GetUtcNow();

            long cursor = repository.GetProjectionCursorAsync(timeout.Token)
                .GetAwaiter().GetResult() ?? 0;
            DateTimeOffset? oldest = repository.GetOldestUnprojectedKubernetesReceiptAsync(
                    cursor, now - KubernetesInventoryProjectionService.ReadWindow, now - _visibilityLag, timeout.Token)
                .GetAwaiter().GetResult();

            if (oldest is null)
            {
                return 0d;
            }

            return Math.Max(0d, (now - oldest.Value).TotalSeconds);
        }
        catch (Exception ex)
        {
            // No measurement rather than a throw: an exception here aborts the whole collection
            // cycle, so one unhealthy query would blind every other instrument in the process.
            _logger.LogWarning(ex, "Could not measure the Kubernetes projection lag");

            return null;
        }
    }
}
