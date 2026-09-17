// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Models.Telemetry;

/// <summary>
/// Pins the display DTOs to the snake_case field names the ingest path actually stores. The stored
/// payload is the received protobuf re-serialized, so a mismatch here is silent: the property simply
/// stays at its default and the UI renders a plausible zero.
/// </summary>
public class TelemetryPayloadSerializationTests
{
    [Test]
    public async Task CpuUsagePayload_StoredPayload_BindsEveryJiffyBucket()
    {
        const string Stored = """
            {"cpu_usage_percent":61,"user_time":34,"system_time":11,"nice_time":1,
             "idle_time":39,"iowait_time":14,"irq_time":3,"softirq_time":2,"steal_time":9}
            """;

        CpuUsagePayload? payload = JsonSerializer.Deserialize<CpuUsagePayload>(Stored, JsonDefaults.SnakeCase);

        await Assert.That(payload).IsNotNull();
        await Assert.That(payload!.CpuUsagePercent).IsEqualTo(61);
        await Assert.That(payload.UserTime).IsEqualTo(34);
        await Assert.That(payload.SystemTime).IsEqualTo(11);
        await Assert.That(payload.NiceTime).IsEqualTo(1);
        await Assert.That(payload.IdleTime).IsEqualTo(39);
        await Assert.That(payload.IowaitTime).IsEqualTo(14);
        await Assert.That(payload.IrqTime).IsEqualTo(3);
        await Assert.That(payload.SoftirqTime).IsEqualTo(2);
        await Assert.That(payload.StealTime).IsEqualTo(9);
    }

    [Test]
    public async Task CpuUsagePayload_PayloadFromAnOlderAgent_StillBindsWithZeroedBuckets()
    {
        // Rows already in the database predate the breakdown. They must keep deserializing, and the
        // absent buckets must read as zero rather than failing the whole detail request.
        CpuUsagePayload? payload = JsonSerializer.Deserialize<CpuUsagePayload>(
            """{"cpu_usage_percent":42}""", JsonDefaults.SnakeCase);

        await Assert.That(payload).IsNotNull();
        await Assert.That(payload!.CpuUsagePercent).IsEqualTo(42);
        await Assert.That(payload.IowaitTime).IsEqualTo(0);
        await Assert.That(payload.StealTime).IsEqualTo(0);
    }

    [Test]
    public async Task MemoryInfoPayload_StoredPayload_BindsTotalsAndSwap()
    {
        const string Stored = """
            {"memory_total":17179869184,"memory_free":2147483648,"memory_available":4294967296,
             "swap_total":8589934592,"swap_free":6442450944}
            """;

        MemoryInfoPayload? payload = JsonSerializer.Deserialize<MemoryInfoPayload>(Stored, JsonDefaults.SnakeCase);

        await Assert.That(payload).IsNotNull();
        await Assert.That(payload!.MemoryTotal).IsEqualTo(17179869184L);
        await Assert.That(payload.MemoryFree).IsEqualTo(2147483648L);
        await Assert.That(payload.MemoryAvailable).IsEqualTo(4294967296L);
        await Assert.That(payload.SwapTotal).IsEqualTo(8589934592L);
        await Assert.That(payload.SwapFree).IsEqualTo(6442450944L);
    }

    [Test]
    public async Task MemoryInfoPayload_SwaplessHostWithNoAvailableField_BindsWithZeroes()
    {
        // A host with no swap reports swap_total 0, which the UI uses to hide the swap line entirely;
        // an absent memory_available must not throw.
        MemoryInfoPayload? payload = JsonSerializer.Deserialize<MemoryInfoPayload>(
            """{"memory_total":17179869184,"memory_free":2147483648,"swap_total":0,"swap_free":0}""",
            JsonDefaults.SnakeCase);

        await Assert.That(payload).IsNotNull();
        await Assert.That(payload!.SwapTotal).IsEqualTo(0L);
        await Assert.That(payload.MemoryAvailable).IsEqualTo(0L);
    }
}
