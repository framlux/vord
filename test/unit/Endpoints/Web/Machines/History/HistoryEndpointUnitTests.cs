// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Globalization;
using System.Text.Json;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;
using Framlux.FleetManagement.Server.Services.History;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Telemetry;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Machines.History;

/// <summary>
/// Unit tests verifying the metric-specific deserialization, value extraction,
/// and aggregation logic used by each history endpoint.
/// </summary>
public class HistoryEndpointUnitTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 6, 0, 0, 0, TimeSpan.Zero);

    // ================================================================
    // Helper methods replicating each endpoint's post-validation logic
    // ================================================================

    /// <summary>
    /// Replicates the CPU endpoint's deserialization and value extraction logic.
    /// </summary>
    private static List<TimestampedValue> ExtractCpuValues(List<MachineTelemetry> rows)
    {
        List<TimestampedValue> values = [];
        foreach (MachineTelemetry row in rows)
        {
            CpuUsagePayload? payload = JsonSerializer.Deserialize<CpuUsagePayload>(row.Payload, JsonDefaults.SnakeCase);
            if (payload is not null)
            {
                values.Add(new TimestampedValue
                {
                    Timestamp = row.ReceivedAt,
                    Value = payload.CpuUsagePercent
                });
            }
        }

        return values;
    }

    /// <summary>
    /// Replicates the Memory endpoint's deserialization and value extraction logic.
    /// </summary>
    private static List<TimestampedValue> ExtractMemoryValues(List<MachineTelemetry> rows)
    {
        List<TimestampedValue> values = [];
        foreach (MachineTelemetry row in rows)
        {
            MemoryUsagePayload? payload = JsonSerializer.Deserialize<MemoryUsagePayload>(row.Payload, JsonDefaults.SnakeCase);
            if (payload is not null)
            {
                values.Add(new TimestampedValue
                {
                    Timestamp = row.ReceivedAt,
                    Value = payload.MemoryUsagePercent
                });
            }
        }

        return values;
    }

    /// <summary>
    /// Replicates the Disk endpoint's deserialization and per-device grouping logic.
    /// Returns a dictionary keyed by device path, each containing timestamped usage entries.
    /// </summary>
    private static Dictionary<string, List<(DateTimeOffset Timestamp, int UsagePercent, string MountPoint)>> ExtractDiskDeviceData(
        List<MachineTelemetry> rows)
    {
        Dictionary<string, List<(DateTimeOffset Timestamp, int UsagePercent, string MountPoint)>> deviceData = new();
        foreach (MachineTelemetry row in rows)
        {
            DiskUsagePayload? payload = JsonSerializer.Deserialize<DiskUsagePayload>(row.Payload, JsonDefaults.SnakeCase);
            if (payload is null)
            {
                continue;
            }

            foreach (DiskUsageEntryDto disk in payload.Disks)
            {
                if (deviceData.ContainsKey(disk.Device) == false)
                {
                    deviceData[disk.Device] = [];
                }

                deviceData[disk.Device].Add((row.ReceivedAt, disk.UsagePercent, disk.Path));
            }
        }

        return deviceData;
    }

    /// <summary>
    /// Replicates the Services endpoint's deserialization and failed count extraction logic.
    /// </summary>
    private static (List<TimestampedValue> FailedValues, List<(DateTimeOffset Timestamp, int FailedCount, int TotalCount)> RawEntries)
        ExtractServiceValues(List<MachineTelemetry> rows)
    {
        List<TimestampedValue> failedValues = [];
        List<(DateTimeOffset Timestamp, int FailedCount, int TotalCount)> rawEntries = [];

        foreach (MachineTelemetry row in rows)
        {
            ServiceStatusPayload? payload = JsonSerializer.Deserialize<ServiceStatusPayload>(row.Payload, JsonDefaults.SnakeCase);
            if (payload is null)
            {
                continue;
            }

            int failedCount = payload.Services.Count(s => string.Equals(s.ActiveState, "failed", StringComparison.OrdinalIgnoreCase));
            int totalCount = payload.Services.Count;

            failedValues.Add(new TimestampedValue
            {
                Timestamp = row.ReceivedAt,
                Value = failedCount
            });

            rawEntries.Add((row.ReceivedAt, failedCount, totalCount));
        }

        return (failedValues, rawEntries);
    }

    /// <summary>
    /// Replicates the SSH endpoint's deserialization and event mapping logic.
    /// </summary>
    private static List<(DateTimeOffset Timestamp, string User, string SourceIp, int SourcePort, string Action, string AuthMethod)>
        ExtractSshEvents(List<MachineTelemetry> rows)
    {
        List<(DateTimeOffset Timestamp, string User, string SourceIp, int SourcePort, string Action, string AuthMethod)> events = [];
        foreach (MachineTelemetry row in rows)
        {
            SshSessionPayload? payload = JsonSerializer.Deserialize<SshSessionPayload>(row.Payload, JsonDefaults.SnakeCase);
            if (payload is null)
            {
                continue;
            }

            DateTimeOffset eventTimestamp = row.ReceivedAt;
            if (DateTimeOffset.TryParse(payload.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsedTimestamp))
            {
                eventTimestamp = parsedTimestamp;
            }

            events.Add((eventTimestamp, payload.User, payload.SourceIp, payload.SourcePort, payload.Action, payload.AuthMethod));
        }

        return events;
    }

    /// <summary>
    /// Creates a MachineTelemetry row with the given payload serialized using snake_case options.
    /// </summary>
    private static MachineTelemetry CreateRow<T>(T payload, DateTimeOffset receivedAt, short telemetryType)
    {
        return new MachineTelemetry
        {
            Id = 0,
            MachineId = 1,
            TenantId = 1,
            TelemetryType = telemetryType,
            Payload = JsonSerializer.Serialize(payload, JsonDefaults.SnakeCase),
            ReceivedAt = receivedAt
        };
    }

    // ================================================================
    // CPU endpoint tests
    // ================================================================

    [Test]
    public async Task Cpu_SnakeCaseRoundTrip_DeserializesCorrectly()
    {
        CpuUsagePayload original = new() { CpuUsagePercent = 73 };
        string json = JsonSerializer.Serialize(original, JsonDefaults.SnakeCase);
        CpuUsagePayload? deserialized = JsonSerializer.Deserialize<CpuUsagePayload>(json, JsonDefaults.SnakeCase);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized!.CpuUsagePercent).IsEqualTo(73);
    }

    [Test]
    public async Task Cpu_UsesCorrectTelemetryTypeConstant()
    {
        // The CPU endpoint uses TelemetryTypeIds.CpuUsage which should be 6
        await Assert.That(TelemetryTypeIds.CpuUsage).IsEqualTo((short)6);
    }

    [Test]
    public async Task Cpu_ExtractsCpuUsagePercent()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(new CpuUsagePayload { CpuUsagePercent = 42 }, BaseTime, TelemetryTypeIds.CpuUsage),
            CreateRow(new CpuUsagePayload { CpuUsagePercent = 88 }, BaseTime.AddSeconds(30), TelemetryTypeIds.CpuUsage)
        ];

        List<TimestampedValue> values = ExtractCpuValues(rows);

        await Assert.That(values.Count).IsEqualTo(2);
        await Assert.That(values[0].Value).IsEqualTo(42.0);
        await Assert.That(values[0].Timestamp).IsEqualTo(BaseTime);
        await Assert.That(values[1].Value).IsEqualTo(88.0);
        await Assert.That(values[1].Timestamp).IsEqualTo(BaseTime.AddSeconds(30));
    }

    [Test]
    public async Task Cpu_NullPayload_SkippedWithoutCrash()
    {
        List<MachineTelemetry> rows =
        [
            new MachineTelemetry
            {
                Id = 0,
                MachineId = 1,
                TenantId = 1,
                TelemetryType = TelemetryTypeIds.CpuUsage,
                Payload = "null",
                ReceivedAt = BaseTime
            },
            CreateRow(new CpuUsagePayload { CpuUsagePercent = 55 }, BaseTime.AddSeconds(30), TelemetryTypeIds.CpuUsage)
        ];

        List<TimestampedValue> values = ExtractCpuValues(rows);

        await Assert.That(values.Count).IsEqualTo(1);
        await Assert.That(values[0].Value).IsEqualTo(55.0);
    }

    [Test]
    public async Task Cpu_AggregationProducesValidSeries()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(new CpuUsagePayload { CpuUsagePercent = 20 }, BaseTime, TelemetryTypeIds.CpuUsage),
            CreateRow(new CpuUsagePayload { CpuUsagePercent = 80 }, BaseTime.AddMinutes(30), TelemetryTypeIds.CpuUsage)
        ];

        List<TimestampedValue> values = ExtractCpuValues(rows);
        AggregatedSeries series = TelemetryAggregator.Aggregate(values, BaseTime, BaseTime.AddHours(1));

        await Assert.That(series.RawPointCount).IsEqualTo(2);
        await Assert.That(series.Stats.Min).IsEqualTo(20.0);
        await Assert.That(series.Stats.Max).IsEqualTo(80.0);
        await Assert.That(series.Stats.Avg).IsEqualTo(50.0);
    }

    // ================================================================
    // Memory endpoint tests
    // ================================================================

    [Test]
    public async Task Memory_SnakeCaseRoundTrip_DeserializesCorrectly()
    {
        MemoryUsagePayload original = new() { MemoryTotal = 16_000_000_000, MemoryUsed = 8_000_000_000, MemoryUsagePercent = 50 };
        string json = JsonSerializer.Serialize(original, JsonDefaults.SnakeCase);
        MemoryUsagePayload? deserialized = JsonSerializer.Deserialize<MemoryUsagePayload>(json, JsonDefaults.SnakeCase);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized!.MemoryUsagePercent).IsEqualTo(50);
        await Assert.That(deserialized.MemoryTotal).IsEqualTo(16_000_000_000);
        await Assert.That(deserialized.MemoryUsed).IsEqualTo(8_000_000_000);
    }

    [Test]
    public async Task Memory_UsesCorrectTelemetryTypeConstant()
    {
        // The Memory endpoint uses TelemetryTypeIds.MemoryUsage which should be 7
        await Assert.That(TelemetryTypeIds.MemoryUsage).IsEqualTo((short)7);
    }

    [Test]
    public async Task Memory_ExtractsMemoryUsagePercent()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(
                new MemoryUsagePayload { MemoryTotal = 16_000_000_000, MemoryUsed = 4_000_000_000, MemoryUsagePercent = 25 },
                BaseTime, TelemetryTypeIds.MemoryUsage),
            CreateRow(
                new MemoryUsagePayload { MemoryTotal = 16_000_000_000, MemoryUsed = 12_000_000_000, MemoryUsagePercent = 75 },
                BaseTime.AddSeconds(30), TelemetryTypeIds.MemoryUsage)
        ];

        List<TimestampedValue> values = ExtractMemoryValues(rows);

        await Assert.That(values.Count).IsEqualTo(2);
        await Assert.That(values[0].Value).IsEqualTo(25.0);
        await Assert.That(values[1].Value).IsEqualTo(75.0);
    }

    [Test]
    public async Task Memory_NullPayload_SkippedWithoutCrash()
    {
        List<MachineTelemetry> rows =
        [
            new MachineTelemetry
            {
                Id = 0,
                MachineId = 1,
                TenantId = 1,
                TelemetryType = TelemetryTypeIds.MemoryUsage,
                Payload = "null",
                ReceivedAt = BaseTime
            }
        ];

        List<TimestampedValue> values = ExtractMemoryValues(rows);

        await Assert.That(values.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Memory_AggregationProducesValidSeries()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(
                new MemoryUsagePayload { MemoryTotal = 16_000_000_000, MemoryUsed = 1_600_000_000, MemoryUsagePercent = 10 },
                BaseTime, TelemetryTypeIds.MemoryUsage),
            CreateRow(
                new MemoryUsagePayload { MemoryTotal = 16_000_000_000, MemoryUsed = 14_400_000_000, MemoryUsagePercent = 90 },
                BaseTime.AddMinutes(30), TelemetryTypeIds.MemoryUsage)
        ];

        List<TimestampedValue> values = ExtractMemoryValues(rows);
        AggregatedSeries series = TelemetryAggregator.Aggregate(values, BaseTime, BaseTime.AddHours(1));

        await Assert.That(series.RawPointCount).IsEqualTo(2);
        await Assert.That(series.Stats.Min).IsEqualTo(10.0);
        await Assert.That(series.Stats.Max).IsEqualTo(90.0);
        await Assert.That(series.Stats.Avg).IsEqualTo(50.0);
    }

    // ================================================================
    // Disk endpoint tests
    // ================================================================

    [Test]
    public async Task Disk_SnakeCaseRoundTrip_DeserializesCorrectly()
    {
        DiskUsagePayload original = new()
        {
            Disks =
            [
                new DiskUsageEntryDto
                {
                    Device = "/dev/sda1",
                    Path = "/",
                    BlocksSize = 4096,
                    Blocks = 100_000,
                    BlocksFree = 50_000,
                    BlocksAvailable = 45_000,
                    BlocksUsed = 50_000,
                    UsagePercent = 50
                }
            ]
        };
        string json = JsonSerializer.Serialize(original, JsonDefaults.SnakeCase);
        DiskUsagePayload? deserialized = JsonSerializer.Deserialize<DiskUsagePayload>(json, JsonDefaults.SnakeCase);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized!.Disks.Count).IsEqualTo(1);
        await Assert.That(deserialized.Disks[0].Device).IsEqualTo("/dev/sda1");
        await Assert.That(deserialized.Disks[0].Path).IsEqualTo("/");
        await Assert.That(deserialized.Disks[0].UsagePercent).IsEqualTo(50);
    }

    [Test]
    public async Task Disk_UsesCorrectTelemetryTypeConstant()
    {
        // The Disk endpoint uses TelemetryTypeIds.DiskUsage which should be 8
        await Assert.That(TelemetryTypeIds.DiskUsage).IsEqualTo((short)8);
    }

    [Test]
    public async Task Disk_ExtractsPerDeviceUsagePercent()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(new DiskUsagePayload
            {
                Disks =
                [
                    new DiskUsageEntryDto { Device = "/dev/sda1", Path = "/", UsagePercent = 60 }
                ]
            }, BaseTime, TelemetryTypeIds.DiskUsage)
        ];

        Dictionary<string, List<(DateTimeOffset Timestamp, int UsagePercent, string MountPoint)>> deviceData = ExtractDiskDeviceData(rows);

        await Assert.That(deviceData.ContainsKey("/dev/sda1")).IsTrue();
        await Assert.That(deviceData["/dev/sda1"].Count).IsEqualTo(1);
        await Assert.That(deviceData["/dev/sda1"][0].UsagePercent).IsEqualTo(60);
        await Assert.That(deviceData["/dev/sda1"][0].MountPoint).IsEqualTo("/");
    }

    [Test]
    public async Task Disk_NullPayload_SkippedWithoutCrash()
    {
        List<MachineTelemetry> rows =
        [
            new MachineTelemetry
            {
                Id = 0,
                MachineId = 1,
                TenantId = 1,
                TelemetryType = TelemetryTypeIds.DiskUsage,
                Payload = "null",
                ReceivedAt = BaseTime
            }
        ];

        Dictionary<string, List<(DateTimeOffset Timestamp, int UsagePercent, string MountPoint)>> deviceData = ExtractDiskDeviceData(rows);

        await Assert.That(deviceData.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Disk_MultiDeviceSeries_ProducesSeparateSeriesPerDevice()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(new DiskUsagePayload
            {
                Disks =
                [
                    new DiskUsageEntryDto { Device = "/dev/sda1", Path = "/", UsagePercent = 45 },
                    new DiskUsageEntryDto { Device = "/dev/sdb1", Path = "/data", UsagePercent = 80 }
                ]
            }, BaseTime, TelemetryTypeIds.DiskUsage),
            CreateRow(new DiskUsagePayload
            {
                Disks =
                [
                    new DiskUsageEntryDto { Device = "/dev/sda1", Path = "/", UsagePercent = 47 },
                    new DiskUsageEntryDto { Device = "/dev/sdb1", Path = "/data", UsagePercent = 82 }
                ]
            }, BaseTime.AddSeconds(30), TelemetryTypeIds.DiskUsage)
        ];

        Dictionary<string, List<(DateTimeOffset Timestamp, int UsagePercent, string MountPoint)>> deviceData = ExtractDiskDeviceData(rows);

        await Assert.That(deviceData.Count).IsEqualTo(2);
        await Assert.That(deviceData.ContainsKey("/dev/sda1")).IsTrue();
        await Assert.That(deviceData.ContainsKey("/dev/sdb1")).IsTrue();
        await Assert.That(deviceData["/dev/sda1"].Count).IsEqualTo(2);
        await Assert.That(deviceData["/dev/sdb1"].Count).IsEqualTo(2);

        // Verify each device's values are independent
        await Assert.That(deviceData["/dev/sda1"][0].UsagePercent).IsEqualTo(45);
        await Assert.That(deviceData["/dev/sda1"][1].UsagePercent).IsEqualTo(47);
        await Assert.That(deviceData["/dev/sda1"][0].MountPoint).IsEqualTo("/");
        await Assert.That(deviceData["/dev/sdb1"][0].UsagePercent).IsEqualTo(80);
        await Assert.That(deviceData["/dev/sdb1"][1].UsagePercent).IsEqualTo(82);
        await Assert.That(deviceData["/dev/sdb1"][0].MountPoint).IsEqualTo("/data");
    }

    [Test]
    public async Task Disk_MultiDeviceAggregation_ProducesIndependentAggregatedSeries()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(new DiskUsagePayload
            {
                Disks =
                [
                    new DiskUsageEntryDto { Device = "/dev/sda1", Path = "/", UsagePercent = 20 },
                    new DiskUsageEntryDto { Device = "/dev/sdb1", Path = "/data", UsagePercent = 90 }
                ]
            }, BaseTime, TelemetryTypeIds.DiskUsage)
        ];

        Dictionary<string, List<(DateTimeOffset Timestamp, int UsagePercent, string MountPoint)>> deviceData = ExtractDiskDeviceData(rows);

        // Aggregate each device independently, matching what the endpoint does
        foreach (KeyValuePair<string, List<(DateTimeOffset Timestamp, int UsagePercent, string MountPoint)>> kvp in deviceData)
        {
            List<TimestampedValue> values = kvp.Value
                .Select(d => new TimestampedValue { Timestamp = d.Timestamp, Value = d.UsagePercent })
                .ToList();

            AggregatedSeries series = TelemetryAggregator.Aggregate(values, BaseTime, BaseTime.AddHours(1));

            await Assert.That(series.RawPointCount).IsEqualTo(1);

            if (kvp.Key == "/dev/sda1")
            {
                await Assert.That(series.Stats.Avg).IsEqualTo(20.0);
            }
            else
            {
                await Assert.That(series.Stats.Avg).IsEqualTo(90.0);
            }
        }
    }

    // ================================================================
    // Services endpoint tests
    // ================================================================

    [Test]
    public async Task Services_SnakeCaseRoundTrip_DeserializesCorrectly()
    {
        ServiceStatusPayload original = new()
        {
            Services =
            [
                new ServiceEntryDto
                {
                    Unit = "nginx.service",
                    LoadState = "loaded",
                    ActiveState = "active",
                    SubState = "running",
                    Description = "A high performance web server"
                }
            ]
        };
        string json = JsonSerializer.Serialize(original, JsonDefaults.SnakeCase);
        ServiceStatusPayload? deserialized = JsonSerializer.Deserialize<ServiceStatusPayload>(json, JsonDefaults.SnakeCase);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized!.Services.Count).IsEqualTo(1);
        await Assert.That(deserialized.Services[0].Unit).IsEqualTo("nginx.service");
        await Assert.That(deserialized.Services[0].ActiveState).IsEqualTo("active");
    }

    [Test]
    public async Task Services_UsesCorrectTelemetryTypeConstant()
    {
        // The Services endpoint uses TelemetryTypeIds.ServiceStatus which should be 12
        await Assert.That(TelemetryTypeIds.ServiceStatus).IsEqualTo((short)12);
    }

    [Test]
    public async Task Services_FailedCountComputed_OnlyCountsFailedActiveState()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(new ServiceStatusPayload
            {
                Services =
                [
                    new ServiceEntryDto { Unit = "nginx.service", ActiveState = "active", SubState = "running" },
                    new ServiceEntryDto { Unit = "mysql.service", ActiveState = "failed", SubState = "failed" },
                    new ServiceEntryDto { Unit = "redis.service", ActiveState = "active", SubState = "running" },
                    new ServiceEntryDto { Unit = "cron.service", ActiveState = "failed", SubState = "failed" },
                    new ServiceEntryDto { Unit = "ssh.service", ActiveState = "inactive", SubState = "dead" }
                ]
            }, BaseTime, TelemetryTypeIds.ServiceStatus)
        ];

        (List<TimestampedValue> failedValues, List<(DateTimeOffset Timestamp, int FailedCount, int TotalCount)> rawEntries) = ExtractServiceValues(rows);

        await Assert.That(failedValues.Count).IsEqualTo(1);
        await Assert.That(failedValues[0].Value).IsEqualTo(2.0);
        await Assert.That(rawEntries[0].FailedCount).IsEqualTo(2);
        await Assert.That(rawEntries[0].TotalCount).IsEqualTo(5);
    }

    [Test]
    public async Task Services_NoFailedServices_ReturnsZeroFailedCount()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(new ServiceStatusPayload
            {
                Services =
                [
                    new ServiceEntryDto { Unit = "nginx.service", ActiveState = "active", SubState = "running" },
                    new ServiceEntryDto { Unit = "mysql.service", ActiveState = "active", SubState = "running" },
                    new ServiceEntryDto { Unit = "redis.service", ActiveState = "inactive", SubState = "dead" }
                ]
            }, BaseTime, TelemetryTypeIds.ServiceStatus)
        ];

        (List<TimestampedValue> failedValues, List<(DateTimeOffset Timestamp, int FailedCount, int TotalCount)> rawEntries) = ExtractServiceValues(rows);

        await Assert.That(failedValues[0].Value).IsEqualTo(0.0);
        await Assert.That(rawEntries[0].FailedCount).IsEqualTo(0);
        await Assert.That(rawEntries[0].TotalCount).IsEqualTo(3);
    }

    [Test]
    public async Task Services_FailedStateCaseInsensitive()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(new ServiceStatusPayload
            {
                Services =
                [
                    new ServiceEntryDto { Unit = "test1.service", ActiveState = "FAILED", SubState = "failed" },
                    new ServiceEntryDto { Unit = "test2.service", ActiveState = "Failed", SubState = "failed" },
                    new ServiceEntryDto { Unit = "test3.service", ActiveState = "failed", SubState = "failed" }
                ]
            }, BaseTime, TelemetryTypeIds.ServiceStatus)
        ];

        (List<TimestampedValue> failedValues, _) = ExtractServiceValues(rows);

        await Assert.That(failedValues[0].Value).IsEqualTo(3.0);
    }

    [Test]
    public async Task Services_NullPayload_SkippedWithoutCrash()
    {
        List<MachineTelemetry> rows =
        [
            new MachineTelemetry
            {
                Id = 0,
                MachineId = 1,
                TenantId = 1,
                TelemetryType = TelemetryTypeIds.ServiceStatus,
                Payload = "null",
                ReceivedAt = BaseTime
            }
        ];

        (List<TimestampedValue> failedValues, List<(DateTimeOffset Timestamp, int FailedCount, int TotalCount)> rawEntries) = ExtractServiceValues(rows);

        await Assert.That(failedValues.Count).IsEqualTo(0);
        await Assert.That(rawEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Services_AggregationProducesValidSeries()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(new ServiceStatusPayload
            {
                Services =
                [
                    new ServiceEntryDto { Unit = "svc1.service", ActiveState = "failed" },
                    new ServiceEntryDto { Unit = "svc2.service", ActiveState = "active" }
                ]
            }, BaseTime, TelemetryTypeIds.ServiceStatus),
            CreateRow(new ServiceStatusPayload
            {
                Services =
                [
                    new ServiceEntryDto { Unit = "svc1.service", ActiveState = "failed" },
                    new ServiceEntryDto { Unit = "svc2.service", ActiveState = "failed" },
                    new ServiceEntryDto { Unit = "svc3.service", ActiveState = "active" }
                ]
            }, BaseTime.AddMinutes(30), TelemetryTypeIds.ServiceStatus)
        ];

        (List<TimestampedValue> failedValues, _) = ExtractServiceValues(rows);
        AggregatedSeries series = TelemetryAggregator.Aggregate(failedValues, BaseTime, BaseTime.AddHours(1));

        await Assert.That(series.RawPointCount).IsEqualTo(2);
        await Assert.That(series.Stats.Min).IsEqualTo(1.0);
        await Assert.That(series.Stats.Max).IsEqualTo(2.0);
    }

    // ================================================================
    // SSH endpoint tests
    // ================================================================

    [Test]
    public async Task Ssh_SnakeCaseRoundTrip_DeserializesCorrectly()
    {
        SshSessionPayload original = new()
        {
            User = "admin",
            SourceIp = "192.168.1.100",
            SourcePort = 54321,
            Action = "connect",
            AuthMethod = "publickey",
            Timestamp = "2026-05-06T12:00:00+00:00"
        };
        string json = JsonSerializer.Serialize(original, JsonDefaults.SnakeCase);
        SshSessionPayload? deserialized = JsonSerializer.Deserialize<SshSessionPayload>(json, JsonDefaults.SnakeCase);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized!.User).IsEqualTo("admin");
        await Assert.That(deserialized.SourceIp).IsEqualTo("192.168.1.100");
        await Assert.That(deserialized.SourcePort).IsEqualTo(54321);
        await Assert.That(deserialized.Action).IsEqualTo("connect");
        await Assert.That(deserialized.AuthMethod).IsEqualTo("publickey");
        await Assert.That(deserialized.Timestamp).IsEqualTo("2026-05-06T12:00:00+00:00");
    }

    [Test]
    public async Task Ssh_UsesCorrectTelemetryTypeConstant()
    {
        // The SSH endpoint uses TelemetryTypeIds.SshSessions which should be 9
        await Assert.That(TelemetryTypeIds.SshSessions).IsEqualTo((short)9);
    }

    [Test]
    public async Task Ssh_AllEventFieldsMappedCorrectly()
    {
        DateTimeOffset expectedTimestamp = new(2026, 5, 6, 14, 30, 0, TimeSpan.Zero);

        List<MachineTelemetry> rows =
        [
            CreateRow(new SshSessionPayload
            {
                User = "deploy",
                SourceIp = "10.0.0.5",
                SourcePort = 12345,
                Action = "disconnect",
                AuthMethod = "password",
                Timestamp = expectedTimestamp.ToString("o")
            }, BaseTime, TelemetryTypeIds.SshSessions)
        ];

        List<(DateTimeOffset Timestamp, string User, string SourceIp, int SourcePort, string Action, string AuthMethod)> events = ExtractSshEvents(rows);

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Timestamp).IsEqualTo(expectedTimestamp);
        await Assert.That(events[0].User).IsEqualTo("deploy");
        await Assert.That(events[0].SourceIp).IsEqualTo("10.0.0.5");
        await Assert.That(events[0].SourcePort).IsEqualTo(12345);
        await Assert.That(events[0].Action).IsEqualTo("disconnect");
        await Assert.That(events[0].AuthMethod).IsEqualTo("password");
    }

    [Test]
    public async Task Ssh_InvalidTimestamp_FallsBackToReceivedAt()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(new SshSessionPayload
            {
                User = "admin",
                SourceIp = "192.168.1.1",
                SourcePort = 22,
                Action = "connect",
                AuthMethod = "publickey",
                Timestamp = "not-a-timestamp"
            }, BaseTime, TelemetryTypeIds.SshSessions)
        ];

        List<(DateTimeOffset Timestamp, string User, string SourceIp, int SourcePort, string Action, string AuthMethod)> events = ExtractSshEvents(rows);

        // Should fall back to ReceivedAt when timestamp cannot be parsed
        await Assert.That(events[0].Timestamp).IsEqualTo(BaseTime);
    }

    [Test]
    public async Task Ssh_NullPayload_SkippedWithoutCrash()
    {
        List<MachineTelemetry> rows =
        [
            new MachineTelemetry
            {
                Id = 0,
                MachineId = 1,
                TenantId = 1,
                TelemetryType = TelemetryTypeIds.SshSessions,
                Payload = "null",
                ReceivedAt = BaseTime
            }
        ];

        List<(DateTimeOffset Timestamp, string User, string SourceIp, int SourcePort, string Action, string AuthMethod)> events = ExtractSshEvents(rows);

        await Assert.That(events.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Ssh_EventCapAt500_ExcessEventsAreTrimmed()
    {
        // Generate 600 events, exceeding the 500 cap
        List<MachineTelemetry> rows = [];
        for (int i = 0; i < 600; i++)
        {
            rows.Add(CreateRow(new SshSessionPayload
            {
                User = $"user{i}",
                SourceIp = "10.0.0.1",
                SourcePort = 22000 + i,
                Action = "connect",
                AuthMethod = "publickey",
                Timestamp = BaseTime.AddMinutes(i).ToString("o")
            }, BaseTime.AddMinutes(i), TelemetryTypeIds.SshSessions));
        }

        // Extract all events (the endpoint does this before capping)
        List<(DateTimeOffset Timestamp, string User, string SourceIp, int SourcePort, string Action, string AuthMethod)> allEvents = ExtractSshEvents(rows);
        int totalEvents = allEvents.Count;

        // Apply the same capping logic as the endpoint: order newest first, take MaxEvents
        List<(DateTimeOffset Timestamp, string User, string SourceIp, int SourcePort, string Action, string AuthMethod)> capped = allEvents
            .OrderByDescending(e => e.Timestamp)
            .Take(SshHistoryEndpoint.MaxEvents)
            .ToList();

        await Assert.That(totalEvents).IsEqualTo(600);
        await Assert.That(capped.Count).IsEqualTo(500);

        // Verify the cap constant matches the expected value
        await Assert.That(SshHistoryEndpoint.MaxEvents).IsEqualTo(500);
    }

    [Test]
    public async Task Ssh_EventsOrderedNewestFirst()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(new SshSessionPayload
            {
                User = "oldest",
                SourceIp = "10.0.0.1",
                SourcePort = 22,
                Action = "connect",
                AuthMethod = "publickey",
                Timestamp = BaseTime.ToString("o")
            }, BaseTime, TelemetryTypeIds.SshSessions),
            CreateRow(new SshSessionPayload
            {
                User = "newest",
                SourceIp = "10.0.0.2",
                SourcePort = 22,
                Action = "connect",
                AuthMethod = "publickey",
                Timestamp = BaseTime.AddHours(1).ToString("o")
            }, BaseTime.AddHours(1), TelemetryTypeIds.SshSessions)
        ];

        List<(DateTimeOffset Timestamp, string User, string SourceIp, int SourcePort, string Action, string AuthMethod)> allEvents = ExtractSshEvents(rows);

        // Apply the same ordering as the endpoint
        List<(DateTimeOffset Timestamp, string User, string SourceIp, int SourcePort, string Action, string AuthMethod)> ordered = allEvents
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        await Assert.That(ordered[0].User).IsEqualTo("newest");
        await Assert.That(ordered[1].User).IsEqualTo("oldest");
    }

    [Test]
    public async Task Ssh_EmptyTimestampString_FallsBackToReceivedAt()
    {
        List<MachineTelemetry> rows =
        [
            CreateRow(new SshSessionPayload
            {
                User = "admin",
                SourceIp = "10.0.0.1",
                SourcePort = 22,
                Action = "connect",
                AuthMethod = "publickey",
                Timestamp = ""
            }, BaseTime, TelemetryTypeIds.SshSessions)
        ];

        List<(DateTimeOffset Timestamp, string User, string SourceIp, int SourcePort, string Action, string AuthMethod)> events = ExtractSshEvents(rows);

        // Empty timestamp should not parse, so it falls back to ReceivedAt
        await Assert.That(events[0].Timestamp).IsEqualTo(BaseTime);
    }
}
