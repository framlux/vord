// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Machines.Projection;
using Framlux.FleetManagement.Services.Core.Telemetry;

namespace Framlux.FleetManagement.Test.Services.Machines.Projection;

/// <summary>
/// Tests for the machine-state batch collapser and its patch contract.
/// </summary>
public class MachineStateBatchCollapserTests
{
    [Test]
    public async Task Patch_WithOnlySummaryBearingType_ReportsNoDetailChanges()
    {
        MachineStatePatch patch = new()
        {
            MachineId = 1,
            LastSeenAt = DateTimeOffset.UnixEpoch,
            CpuUsage = new CpuUsageFragment(CpuUsagePercent: 42),
        };

        await Assert.That(patch.HasDetailChanges).IsFalse();
    }

    [Test]
    public async Task Patch_WithDetailBearingType_ReportsDetailChanges()
    {
        MachineStatePatch patch = new()
        {
            MachineId = 1,
            LastSeenAt = DateTimeOffset.UnixEpoch,
            DiskInfo = new DiskInfoFragment(DiskInfos: "[]"),
        };

        await Assert.That(patch.HasDetailChanges).IsTrue();
    }

    [Test]
    public async Task Collapse_MultipleRowsOfSameType_KeepsLatestByReceivedAt()
    {
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        List<MachineTelemetry> batch =
        [
            Row(1, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 10 }""", t0),
            Row(2, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 90 }""", t0.AddMinutes(5)),
            Row(3, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 50 }""", t0.AddMinutes(2)),
        ];

        CollapseResult result = MachineStateBatchCollapser.Collapse(batch);

        MachineStatePatch patch = result.Patches.Single();

        // Latest by ReceivedAt is the t0+5m row (value 90), NOT the highest-Id row (value 50).
        await Assert.That(patch.CpuUsage!.CpuUsagePercent).IsEqualTo(90);
    }

    [Test]
    public async Task Collapse_OfflineBackfill_DoesNotLetOlderReadingWinDespiteHigherId()
    {
        // Intent: a backfilled row (higher Id, OLDER ReceivedAt) must not overwrite the fresher reading.
        DateTimeOffset fresh = DateTimeOffset.UnixEpoch.AddHours(10);
        DateTimeOffset stale = DateTimeOffset.UnixEpoch.AddHours(1);
        List<MachineTelemetry> batch =
        [
            Row(10, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 70 }""", fresh),
            Row(11, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 5 }""", stale),
        ];

        CollapseResult result = MachineStateBatchCollapser.Collapse(batch);

        await Assert.That(result.Patches.Single().CpuUsage!.CpuUsagePercent).IsEqualTo(70);
    }

    [Test]
    public async Task Collapse_LastSeenAt_IsMaxReceivedAtAcrossAllTypes()
    {
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        List<MachineTelemetry> batch =
        [
            Row(1, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 1 }""", t0.AddMinutes(9)),
            Row(2, 100, TelemetryTypeIds.MemoryUsage, """{ "memory_usage_percent": 1 }""", t0.AddMinutes(3)),
            Row(3, 100, TelemetryTypeIds.PackageUpdates, """{ "pending_updates": 1, "security_updates": 0 }""", t0.AddMinutes(1)),
        ];

        CollapseResult result = MachineStateBatchCollapser.Collapse(batch);

        await Assert.That(result.Patches.Single().LastSeenAt).IsEqualTo(t0.AddMinutes(9));
    }

    [Test]
    public async Task Collapse_MultipleMachines_ProducesOnePatchEach()
    {
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        List<MachineTelemetry> batch =
        [
            Row(1, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 11 }""", t0),
            Row(2, 200, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 22 }""", t0),
        ];

        CollapseResult result = MachineStateBatchCollapser.Collapse(batch);

        await Assert.That(result.Patches.Count).IsEqualTo(2);
        await Assert.That(result.Patches.Single(p => p.MachineId == 100).CpuUsage!.CpuUsagePercent).IsEqualTo(11);
        await Assert.That(result.Patches.Single(p => p.MachineId == 200).CpuUsage!.CpuUsagePercent).IsEqualTo(22);
    }

    [Test]
    public async Task Collapse_MalformedRow_IsRecordedAndOtherTypesStillApply()
    {
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        List<MachineTelemetry> batch =
        [
            Row(1, 100, TelemetryTypeIds.CpuUsage, "this is not json", t0),
            Row(2, 100, TelemetryTypeIds.MemoryUsage, """{ "memory_usage_percent": 33 }""", t0),
        ];

        CollapseResult result = MachineStateBatchCollapser.Collapse(batch);

        MachineStatePatch patch = result.Patches.Single();
        await Assert.That(patch.CpuUsage).IsNull();
        await Assert.That(patch.MemoryUsage!.MemoryUsagePercent).IsEqualTo(33);
        await Assert.That(result.Skipped.Single().RowId).IsEqualTo(1);
        await Assert.That(result.Skipped.Single().TelemetryType).IsEqualTo(TelemetryTypeIds.CpuUsage);

        // LastSeenAt still reflects the machine's max ReceivedAt even though a row was skipped.
        await Assert.That(patch.LastSeenAt).IsEqualTo(t0);
    }

    [Test]
    public async Task Collapse_WrongTypedFieldRow_IsRecordedAndOtherTypesStillApply()
    {
        // Intent: a row with structurally valid JSON but a wrong-typed field (a string where an int
        // is expected) makes the typed accessor throw. The collapser must skip it as a poison row,
        // not let the exception escape and wedge the batch. A healthy type still applies.
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        List<MachineTelemetry> batch =
        [
            Row(1, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": "not-a-number" }""", t0),
            Row(2, 100, TelemetryTypeIds.MemoryUsage, """{ "memory_usage_percent": 33 }""", t0),
        ];

        CollapseResult result = MachineStateBatchCollapser.Collapse(batch);

        MachineStatePatch patch = result.Patches.Single();
        await Assert.That(patch.CpuUsage).IsNull();
        await Assert.That(patch.MemoryUsage!.MemoryUsagePercent).IsEqualTo(33);
        await Assert.That(result.Skipped.Single().RowId).IsEqualTo(1);
        await Assert.That(result.Skipped.Single().TelemetryType).IsEqualTo(TelemetryTypeIds.CpuUsage);
    }

    [Test]
    public async Task Collapse_EmptyBatch_ProducesNoPatches()
    {
        CollapseResult result = MachineStateBatchCollapser.Collapse([]);

        await Assert.That(result.Patches.Count).IsEqualTo(0);
        await Assert.That(result.Skipped.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Collapse_TwoTypesForOneMachine_BothLandOnSamePatch()
    {
        // Intent: distinct telemetry types for one machine collapse onto a single patch.
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        List<MachineTelemetry> batch =
        [
            Row(1, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 44 }""", t0),
            Row(2, 100, TelemetryTypeIds.DiskInfo, """[{"name":"sda"}]""", t0),
        ];

        CollapseResult result = MachineStateBatchCollapser.Collapse(batch);

        MachineStatePatch patch = result.Patches.Single();
        await Assert.That(patch.CpuUsage!.CpuUsagePercent).IsEqualTo(44);
        await Assert.That(patch.DiskInfo!.DiskInfos).IsEqualTo("""[{"name":"sda"}]""");
        await Assert.That(patch.HasDetailChanges).IsTrue();
    }

    [Test]
    public async Task Collapse_EveryTelemetryType_MapsToItsFragment()
    {
        // Intent: every one of the 12 telemetry types is wired into the switch; a missing
        // case would leave the corresponding fragment null and fail this test.
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        List<MachineTelemetry> batch =
        [
            Row(1, 100, TelemetryTypeIds.SystemInfo, """{ "hostname": "web-01" }""", t0),
            Row(2, 100, TelemetryTypeIds.OsVersion, """{ "os_name": "Ubuntu", "os_version": "24.04", "kernel": "6.8" }""", t0),
            Row(3, 100, TelemetryTypeIds.CpuInfo, """{ "cpu_type": "x86_64", "physical_cpus": 2, "logical_cpus": 8 }""", t0),
            Row(4, 100, TelemetryTypeIds.MemoryInfo, """{ "swap_total_bytes": 1024, "swap_free_bytes": 512 }""", t0),
            Row(5, 100, TelemetryTypeIds.DiskInfo, """[{"name":"sda"}]""", t0),
            Row(6, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 12 }""", t0),
            Row(7, 100, TelemetryTypeIds.MemoryUsage, """{ "memory_usage_percent": 34, "memory_used": 2048 }""", t0),
            Row(8, 100, TelemetryTypeIds.DiskUsage, """{ "disks": [ { "usage_percent": 55 } ] }""", t0),
            Row(9, 100, TelemetryTypeIds.SshSessions, """[{"user":"root"}]""", t0),
            Row(10, 100, TelemetryTypeIds.HardwareHealth, """{ "fans": [ { "rpm": 0 } ] }""", t0),
            Row(11, 100, TelemetryTypeIds.PackageUpdates, """{ "pending_updates": 7, "security_updates": 2 }""", t0),
            Row(12, 100, TelemetryTypeIds.ServiceStatus, """{ "total_services": 50, "failed_services": 1 }""", t0),
        ];

        CollapseResult result = MachineStateBatchCollapser.Collapse(batch);

        MachineStatePatch patch = result.Patches.Single();
        await Assert.That(result.Skipped.Count).IsEqualTo(0);
        await Assert.That(patch.SystemInfo).IsNotNull();
        await Assert.That(patch.OsVersion).IsNotNull();
        await Assert.That(patch.CpuInfo).IsNotNull();
        await Assert.That(patch.MemoryInfo).IsNotNull();
        await Assert.That(patch.DiskInfo).IsNotNull();
        await Assert.That(patch.CpuUsage).IsNotNull();
        await Assert.That(patch.MemoryUsage).IsNotNull();
        await Assert.That(patch.DiskUsage).IsNotNull();
        await Assert.That(patch.SshSessions).IsNotNull();
        await Assert.That(patch.HardwareHealth).IsNotNull();
        await Assert.That(patch.PackageUpdates).IsNotNull();
        await Assert.That(patch.ServiceStatus).IsNotNull();
    }

    [Test]
    public async Task Collapse_UnknownTelemetryType_IsIgnored()
    {
        // Intent: an unrecognized telemetry type falls through to the default branch and
        // produces no fragment and no skip, but the machine is still seen.
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        List<MachineTelemetry> batch =
        [
            Row(1, 100, 999, """{ "anything": true }""", t0),
        ];

        CollapseResult result = MachineStateBatchCollapser.Collapse(batch);

        MachineStatePatch patch = result.Patches.Single();
        await Assert.That(patch.HasDetailChanges).IsFalse();
        await Assert.That(patch.CpuUsage).IsNull();
        await Assert.That(result.Skipped.Count).IsEqualTo(0);
        await Assert.That(patch.LastSeenAt).IsEqualTo(t0);
    }

    private static MachineTelemetry Row(long id, long machineId, short type, string payload, DateTimeOffset receivedAt) =>
        new()
        {
            Id = id,
            MachineId = machineId,
            TenantId = 1,
            TelemetryType = type,
            Payload = payload,
            ReceivedAt = receivedAt,
        };
}
