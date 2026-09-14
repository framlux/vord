// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Reports how far behind each projection shard this process tracks is, in seconds.
/// </summary>
/// <remarks>
/// Lag is the age of the oldest row the shard has not projected and that the projection read would
/// actually pick up, so a shard with nothing to do reports zero rather than the ever-growing age of
/// whatever it last handled. The cursor itself is a row id and cannot answer this, which is why a
/// dedicated query exists — and why that query repeats the projection read's predicates exactly,
/// since a row the loop will never read is not lag.
/// </remarks>
public sealed class ProjectionMetrics : IObservableMetrics
{
    /// <summary>
    /// How long a single shard's measurement may take before it is abandoned. The callback runs on
    /// the exporter's collection thread, so a hung query without this would stall every instrument
    /// in the process, not just this one.
    /// </summary>
    private static readonly TimeSpan MeasurementTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<int, byte> _trackedShards = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProjectionMetrics> _logger;
    private readonly StreamingOptions _streamingOptions;

    /// <summary>
    /// Creates the projection instrument.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    /// <param name="scopeFactory">Used to resolve a scoped repository per observation.</param>
    /// <param name="timeProvider">The clock lag is measured against.</param>
    /// <param name="streamingOptions">Streaming options carrying the shard count and safety lag.</param>
    /// <param name="logger">The logger.</param>
    public ProjectionMetrics(
        IMeterFactory meterFactory,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<StreamingOptions> streamingOptions,
        ILogger<ProjectionMetrics> logger)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(streamingOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _streamingOptions = streamingOptions.Value;

        Meter meter = meterFactory.Create(VordMeter.Name);

        meter.CreateObservableGauge(
            "vord.projection.lag_seconds",
            ObserveLag,
            unit: "s",
            description: "Age of the oldest telemetry row a projection shard has not yet projected.");
    }

    /// <summary>
    /// Declares that this process tracks the given projection shard, so its lag is reported.
    /// </summary>
    /// <param name="shardIndex">The shard to report on.</param>
    public void TrackShard(int shardIndex)
    {
        _trackedShards[shardIndex] = 0;
    }

    private IEnumerable<Measurement<double>> ObserveLag()
    {
        List<Measurement<double>> measurements = [];

        foreach (int shardIndex in _trackedShards.Keys)
        {
            double? lag = MeasureShard(shardIndex);
            if (lag is null)
            {
                continue;
            }

            measurements.Add(new Measurement<double>(
                lag.Value,
                new KeyValuePair<string, object?>(
                    "shard_index",
                    shardIndex.ToString(CultureInfo.InvariantCulture))));
        }

        return measurements;
    }

    private double? MeasureShard(int shardIndex)
    {
        try
        {
            using CancellationTokenSource timeout = new(MeasurementTimeout);
            using IServiceScope scope = _scopeFactory.CreateScope();
            IMachineStateRepository repository = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset streamingWindow = now.AddDays(-MachineStateStreamingService.StreamingWindowDays);
            DateTimeOffset visibilityCutoff = now.AddSeconds(-_streamingOptions.VisibilityLagSeconds);

            // Clamped the same way the shard registration clamps it, so the gauge measures the row
            // set the loop actually projects even under a misconfigured shard count.
            int shardCount = Math.Max(1, _streamingOptions.ShardCount);

            long cursor = repository.GetProjectionCursorAsync(shardIndex, timeout.Token)
                .GetAwaiter().GetResult() ?? 0;
            DateTimeOffset? oldest = repository.GetOldestUnprojectedReceiptAsync(
                    cursor, streamingWindow, visibilityCutoff, shardIndex, shardCount, timeout.Token)
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
            _logger.LogWarning(ex, "Could not measure projection lag for shard {ShardIndex}", shardIndex);

            return null;
        }
    }
}
