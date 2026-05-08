// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.History;

namespace Framlux.FleetManagement.Test.Services.History;

/// <summary>
/// Tests for <see cref="TelemetryAggregator"/>.
/// </summary>
public class TelemetryAggregatorTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 6, 0, 0, 0, TimeSpan.Zero);

    private static List<TimestampedValue> GenerateValues(int count, DateTimeOffset start, int intervalSeconds, double baseValue = 50.0, double variance = 10.0)
    {
        List<TimestampedValue> values = new(count);
        for (int i = 0; i < count; i++)
        {
            values.Add(new TimestampedValue
            {
                Timestamp = start.AddSeconds(i * intervalSeconds),
                Value = baseValue + ((i % 2 == 0) ? variance : -variance)
            });
        }

        return values;
    }

    // ========== Empty dataset ==========

    [Test]
    public async Task Aggregate_EmptyDataset_ReturnsEmptyPointsAndZeroedStats()
    {
        AggregatedSeries result = TelemetryAggregator.Aggregate(
            [],
            BaseTime,
            BaseTime.AddHours(1));

        await Assert.That(result.Points.Count).IsEqualTo(0);
        await Assert.That(result.Stats.Min).IsEqualTo(0);
        await Assert.That(result.Stats.Avg).IsEqualTo(0);
        await Assert.That(result.Stats.Max).IsEqualTo(0);
        await Assert.That(result.Stats.P95).IsEqualTo(0);
        await Assert.That(result.BucketSeconds).IsEqualTo(0);
        await Assert.That(result.RawPointCount).IsEqualTo(0);
    }

    // ========== Single data point ==========

    [Test]
    public async Task Aggregate_SinglePoint_ReturnsOnePointWithStatsAllEqualToValue()
    {
        List<TimestampedValue> values =
        [
            new() { Timestamp = BaseTime.AddMinutes(30), Value = 42.0 }
        ];

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, BaseTime.AddHours(1));

        await Assert.That(result.Points.Count).IsEqualTo(1);
        await Assert.That(result.Points[0].Value).IsEqualTo(42.0);
        await Assert.That(result.Stats.Min).IsEqualTo(42.0);
        await Assert.That(result.Stats.Avg).IsEqualTo(42.0);
        await Assert.That(result.Stats.Max).IsEqualTo(42.0);
        await Assert.That(result.Stats.P95).IsEqualTo(42.0);
        await Assert.That(result.BucketSeconds).IsEqualTo(0);
        await Assert.That(result.RawPointCount).IsEqualTo(1);
    }

    // ========== Raw passthrough when count <= maxPoints ==========

    [Test]
    public async Task Aggregate_UnderMaxPoints_ReturnsRawDataUnbucketed()
    {
        List<TimestampedValue> values = GenerateValues(100, BaseTime, 30);
        DateTimeOffset to = BaseTime.AddSeconds(100 * 30);

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, to, maxPoints: 300);

        await Assert.That(result.Points.Count).IsEqualTo(100);
        await Assert.That(result.BucketSeconds).IsEqualTo(0);
        await Assert.That(result.RawPointCount).IsEqualTo(100);

        for (int i = 0; i < values.Count; i++)
        {
            await Assert.That(result.Points[i].Timestamp).IsEqualTo(values[i].Timestamp);
            await Assert.That(result.Points[i].Value).IsEqualTo(values[i].Value);
        }
    }

    // ========== Boundary: exactly 300 points returns raw (no bucketing) ==========

    [Test]
    public async Task Aggregate_ExactlyMaxPoints_ReturnsRawWithoutBucketing()
    {
        List<TimestampedValue> values = GenerateValues(300, BaseTime, 30);
        DateTimeOffset to = BaseTime.AddSeconds(300 * 30);

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, to, maxPoints: 300);

        await Assert.That(result.Points.Count).IsEqualTo(300);
        await Assert.That(result.BucketSeconds).IsEqualTo(0);
        await Assert.That(result.RawPointCount).IsEqualTo(300);
    }

    // ========== Boundary: 301 points triggers bucketing ==========

    [Test]
    public async Task Aggregate_OneOverMaxPoints_TriggersBucketing()
    {
        List<TimestampedValue> values = GenerateValues(301, BaseTime, 30);
        DateTimeOffset to = BaseTime.AddSeconds(301 * 30);

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, to, maxPoints: 300);

        await Assert.That(result.Points.Count).IsLessThanOrEqualTo(300);
        await Assert.That(result.BucketSeconds).IsGreaterThan(0);
        await Assert.That(result.RawPointCount).IsEqualTo(301);
    }

    // ========== Bucketing produces correct averages ==========

    [Test]
    public async Task Aggregate_BucketedValues_ProducesCorrectAverages()
    {
        // 4 points, max 2 buckets, covering 200 seconds total
        // Bucket size = 200 / 2 = 100 seconds
        // Bucket 0 (0-99s): values at t=0 (10.0) and t=50 (20.0) -> avg 15.0
        // Bucket 1 (100-199s): values at t=100 (30.0) and t=150 (40.0) -> avg 35.0
        List<TimestampedValue> values =
        [
            new() { Timestamp = BaseTime, Value = 10.0 },
            new() { Timestamp = BaseTime.AddSeconds(50), Value = 20.0 },
            new() { Timestamp = BaseTime.AddSeconds(100), Value = 30.0 },
            new() { Timestamp = BaseTime.AddSeconds(150), Value = 40.0 }
        ];

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, BaseTime.AddSeconds(200), maxPoints: 2);

        await Assert.That(result.Points.Count).IsEqualTo(2);
        await Assert.That(result.Points[0].Value).IsEqualTo(15.0);
        await Assert.That(result.Points[1].Value).IsEqualTo(35.0);
        await Assert.That(result.BucketSeconds).IsEqualTo(100);
    }

    // ========== Stats computed correctly ==========

    [Test]
    public async Task Aggregate_StatsComputedFromRawValues()
    {
        List<TimestampedValue> values =
        [
            new() { Timestamp = BaseTime, Value = 10.0 },
            new() { Timestamp = BaseTime.AddSeconds(30), Value = 30.0 },
            new() { Timestamp = BaseTime.AddSeconds(60), Value = 50.0 },
            new() { Timestamp = BaseTime.AddSeconds(90), Value = 70.0 },
            new() { Timestamp = BaseTime.AddSeconds(120), Value = 90.0 }
        ];

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, BaseTime.AddSeconds(150));

        await Assert.That(result.Stats.Min).IsEqualTo(10.0);
        await Assert.That(result.Stats.Max).IsEqualTo(90.0);
        await Assert.That(result.Stats.Avg).IsEqualTo(50.0);
        // P95 of [10, 30, 50, 70, 90]: index = 4 * 0.95 = 3.8 -> lerp(70, 90, 0.8) = 86.0
        await Assert.That(result.Stats.P95).IsEqualTo(86.0);
    }

    // ========== All identical values ==========

    [Test]
    public async Task Aggregate_AllIdenticalValues_StatsAllEqual()
    {
        List<TimestampedValue> values = Enumerable.Range(0, 50)
            .Select(i => new TimestampedValue
            {
                Timestamp = BaseTime.AddSeconds(i * 30),
                Value = 55.0
            })
            .ToList();

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, BaseTime.AddSeconds(50 * 30));

        await Assert.That(result.Stats.Min).IsEqualTo(55.0);
        await Assert.That(result.Stats.Avg).IsEqualTo(55.0);
        await Assert.That(result.Stats.Max).IsEqualTo(55.0);
        await Assert.That(result.Stats.P95).IsEqualTo(55.0);
    }

    // ========== Extreme values ==========

    [Test]
    public async Task Aggregate_ExtremeValues_HandledCorrectly()
    {
        List<TimestampedValue> values =
        [
            new() { Timestamp = BaseTime, Value = 0.0 },
            new() { Timestamp = BaseTime.AddSeconds(30), Value = 100.0 },
            new() { Timestamp = BaseTime.AddSeconds(60), Value = -5.0 }
        ];

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, BaseTime.AddSeconds(90));

        await Assert.That(result.Stats.Min).IsEqualTo(-5.0);
        await Assert.That(result.Stats.Max).IsEqualTo(100.0);
        await Assert.That(result.Points.Count).IsEqualTo(3);
    }

    // ========== Non-uniform time distribution ==========

    [Test]
    public async Task Aggregate_NonUniformDistribution_EmptyBucketsAreOmitted()
    {
        // All points clustered in first 10 seconds of a 1000-second range
        // With maxPoints=5, bucket size = 200s
        // Only bucket 0 (0-199s) should have data
        List<TimestampedValue> values =
        [
            new() { Timestamp = BaseTime, Value = 10.0 },
            new() { Timestamp = BaseTime.AddSeconds(2), Value = 20.0 },
            new() { Timestamp = BaseTime.AddSeconds(4), Value = 30.0 },
            new() { Timestamp = BaseTime.AddSeconds(6), Value = 40.0 },
            new() { Timestamp = BaseTime.AddSeconds(8), Value = 50.0 },
            new() { Timestamp = BaseTime.AddSeconds(10), Value = 60.0 }
        ];

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, BaseTime.AddSeconds(1000), maxPoints: 5);

        // All 6 points fall in bucket 0, so only 1 bucket in output
        await Assert.That(result.Points.Count).IsEqualTo(1);
        await Assert.That(result.Points[0].Value).IsEqualTo(35.0);
        await Assert.That(result.RawPointCount).IsEqualTo(6);
    }

    // ========== P95 with small datasets ==========

    [Test]
    public async Task ComputeStats_TwoValues_P95IsInterpolatedCorrectly()
    {
        List<TimestampedValue> values =
        [
            new() { Timestamp = BaseTime, Value = 10.0 },
            new() { Timestamp = BaseTime.AddSeconds(30), Value = 90.0 }
        ];

        AggregationStats stats = TelemetryAggregator.ComputeStats(values);

        // P95 of [10, 90]: index = 1 * 0.95 = 0.95 -> lerp(10, 90, 0.95) = 86.0
        await Assert.That(stats.P95).IsEqualTo(86.0);
        await Assert.That(stats.Min).IsEqualTo(10.0);
        await Assert.That(stats.Max).IsEqualTo(90.0);
        await Assert.That(stats.Avg).IsEqualTo(50.0);
    }

    [Test]
    public async Task ComputeStats_ThreeValues_P95IsCorrect()
    {
        List<TimestampedValue> values =
        [
            new() { Timestamp = BaseTime, Value = 0.0 },
            new() { Timestamp = BaseTime.AddSeconds(30), Value = 50.0 },
            new() { Timestamp = BaseTime.AddSeconds(60), Value = 100.0 }
        ];

        AggregationStats stats = TelemetryAggregator.ComputeStats(values);

        // P95 of [0, 50, 100]: index = 2 * 0.95 = 1.9 -> lerp(50, 100, 0.9) = 95.0
        await Assert.That(stats.P95).IsEqualTo(95.0);
    }

    // ========== Large dataset bucketing correctness ==========

    [Test]
    public async Task Aggregate_LargeDataset_RespectsMaxPointsBudget()
    {
        // 2880 points (24h at 30s intervals)
        List<TimestampedValue> values = GenerateValues(2880, BaseTime, 30);
        DateTimeOffset to = BaseTime.AddHours(24);

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, to, maxPoints: 300);

        await Assert.That(result.Points.Count).IsLessThanOrEqualTo(300);
        await Assert.That(result.BucketSeconds).IsGreaterThan(0);
        await Assert.That(result.RawPointCount).IsEqualTo(2880);
        // Bucket size = ceil(86400 / 300) = 288 seconds (divides evenly)
        await Assert.That(result.BucketSeconds).IsEqualTo(288);
    }

    // ========== Bucket timestamps are ordered ascending ==========

    [Test]
    public async Task Aggregate_BucketedOutput_TimestampsAreOrderedAscending()
    {
        List<TimestampedValue> values = GenerateValues(600, BaseTime, 30);
        DateTimeOffset to = BaseTime.AddSeconds(600 * 30);

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, to, maxPoints: 300);

        for (int i = 1; i < result.Points.Count; i++)
        {
            await Assert.That(result.Points[i].Timestamp > result.Points[i - 1].Timestamp).IsTrue();
        }
    }

    // ========== Stats are computed from raw values, not bucketed values ==========

    [Test]
    public async Task Aggregate_StatsFromRawNotBucketed_MinMaxPreserved()
    {
        // Create values where bucketing would average away the extremes
        List<TimestampedValue> values = [];
        for (int i = 0; i < 400; i++)
        {
            double value = 50.0;
            if (i == 100)
            {
                value = 5.0;
            }

            if (i == 200)
            {
                value = 99.0;
            }

            values.Add(new TimestampedValue
            {
                Timestamp = BaseTime.AddSeconds(i * 30),
                Value = value
            });
        }

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, BaseTime.AddSeconds(400 * 30), maxPoints: 50);

        // Stats should reflect the raw extremes, even though bucketing averaged them
        await Assert.That(result.Stats.Min).IsEqualTo(5.0);
        await Assert.That(result.Stats.Max).IsEqualTo(99.0);
    }

    // ========== Values are rounded to 1 decimal place ==========

    [Test]
    public async Task Aggregate_BucketedValues_RoundedToOneDecimal()
    {
        List<TimestampedValue> values =
        [
            new() { Timestamp = BaseTime, Value = 33.333 },
            new() { Timestamp = BaseTime.AddSeconds(10), Value = 66.666 },
            new() { Timestamp = BaseTime.AddSeconds(20), Value = 11.111 },
            new() { Timestamp = BaseTime.AddSeconds(100), Value = 22.222 },
            new() { Timestamp = BaseTime.AddSeconds(110), Value = 44.444 },
            new() { Timestamp = BaseTime.AddSeconds(120), Value = 88.888 }
        ];

        AggregatedSeries result = TelemetryAggregator.Aggregate(values, BaseTime, BaseTime.AddSeconds(200), maxPoints: 2);

        // Bucket 0: avg(33.333, 66.666, 11.111) = 37.0367 -> rounded to 37.0
        // Bucket 1: avg(22.222, 44.444, 88.888) = 51.8513 -> rounded to 51.9
        await Assert.That(result.Points[0].Value).IsEqualTo(37.0);
        await Assert.That(result.Points[1].Value).IsEqualTo(51.9);
    }
}
