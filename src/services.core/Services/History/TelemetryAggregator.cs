// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.History;

/// <summary>
/// Aggregates raw telemetry data points into time-bucketed series with statistics.
/// </summary>
public static class TelemetryAggregator
{
    /// <summary>
    /// The default maximum number of data points to return per series.
    /// </summary>
    public const int DefaultMaxPoints = 300;

    /// <summary>
    /// Aggregates a list of timestamped values into a bounded series.
    /// If the input count exceeds <paramref name="maxPoints"/>, values are grouped into
    /// time buckets and averaged. Otherwise, raw values are returned as-is.
    /// Statistics (min, avg, max, p95) are always computed from the full raw dataset.
    /// </summary>
    /// <param name="values">Raw timestamped values, must be ordered by timestamp ascending.</param>
    /// <param name="from">Start of the time range (inclusive).</param>
    /// <param name="to">End of the time range (exclusive).</param>
    /// <param name="maxPoints">Maximum number of points to return.</param>
    /// <returns>An aggregated series with points, stats, bucket size, and raw count.</returns>
    public static AggregatedSeries Aggregate(List<TimestampedValue> values, DateTimeOffset from, DateTimeOffset to, int maxPoints = DefaultMaxPoints)
    {
        if (values.Count == 0)
        {
            return new AggregatedSeries
            {
                Points = [],
                Stats = new AggregationStats { Min = 0, Avg = 0, Max = 0, P95 = 0 },
                BucketSeconds = 0,
                RawPointCount = 0
            };
        }

        AggregationStats stats = ComputeStats(values);

        if (values.Count <= maxPoints)
        {
            List<AggregatedPoint> rawPoints = values
                .Select(v => new AggregatedPoint { Timestamp = v.Timestamp, Value = v.Value })
                .ToList();

            return new AggregatedSeries
            {
                Points = rawPoints,
                Stats = stats,
                BucketSeconds = 0,
                RawPointCount = values.Count
            };
        }

        double totalSeconds = (to - from).TotalSeconds;
        int bucketSeconds = Math.Max(1, (int)Math.Ceiling(totalSeconds / maxPoints));
        List<AggregatedPoint> bucketedPoints = BucketValues(values, from, bucketSeconds);

        return new AggregatedSeries
        {
            Points = bucketedPoints,
            Stats = stats,
            BucketSeconds = bucketSeconds,
            RawPointCount = values.Count
        };
    }

    /// <summary>
    /// Computes min, avg, max, and p95 statistics from a list of values.
    /// </summary>
    /// <param name="values">Non-empty list of timestamped values.</param>
    /// <returns>Computed statistics.</returns>
    public static AggregationStats ComputeStats(List<TimestampedValue> values)
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        double sum = 0;

        foreach (TimestampedValue v in values)
        {
            if (v.Value < min)
            {
                min = v.Value;
            }

            if (v.Value > max)
            {
                max = v.Value;
            }

            sum += v.Value;
        }

        double avg = sum / values.Count;
        double p95 = ComputePercentile(values, 0.95);

        return new AggregationStats
        {
            Min = Math.Round(min, 1),
            Avg = Math.Round(avg, 1),
            Max = Math.Round(max, 1),
            P95 = Math.Round(p95, 1)
        };
    }

    private static double ComputePercentile(List<TimestampedValue> values, double percentile)
    {
        List<double> sorted = values.Select(v => v.Value).OrderBy(v => v).ToList();
        double index = (sorted.Count - 1) * percentile;
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);

        if (lower == upper)
        {
            return sorted[lower];
        }

        double fraction = index - lower;

        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    private static List<AggregatedPoint> BucketValues(List<TimestampedValue> values, DateTimeOffset from, int bucketSeconds)
    {
        long fromEpoch = from.ToUnixTimeSeconds();
        Dictionary<long, List<double>> buckets = new();

        foreach (TimestampedValue v in values)
        {
            long elapsed = v.Timestamp.ToUnixTimeSeconds() - fromEpoch;
            long alignedOffset = elapsed / bucketSeconds * bucketSeconds;
            long bucketKey = alignedOffset + fromEpoch;

            if (buckets.TryGetValue(bucketKey, out List<double>? bucket) == false)
            {
                bucket = [];
                buckets[bucketKey] = bucket;
            }

            bucket.Add(v.Value);
        }

        return buckets
            .OrderBy(kv => kv.Key)
            .Select(kv => new AggregatedPoint
            {
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(kv.Key),
                Value = Math.Round(kv.Value.Average(), 1)
            })
            .ToList();
    }
}
