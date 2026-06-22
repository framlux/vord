// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Machines.Projection;

namespace Framlux.FleetManagement.Test.Services.Machines.Projection;

/// <summary>
/// Tests for <see cref="TelemetryPayloadParser"/>. These assert the documented projection
/// values for each telemetry type, malformed-payload handling, and the two computed columns.
/// The JSON property names mirror the production payloads parsed by the streaming service.
/// </summary>
public class TelemetryPayloadParserTests
{
    [Test]
    public async Task TryParseSystemInfo_FullPayload_MapsEverySummaryAndDetailField()
    {
        string payload = """
        {
          "hostname": "web-01", "hardware_model": "PowerEdge R740",
          "hardware_vendor": "Dell", "hardware_serial": "SVC123",
          "cpu_brand": "Xeon", "cpu_cores": 16,
          "memory_total_bytes": 34359738368, "uptime_seconds": 1000,
          "bios_version": "2.1.0", "ip_addresses": ["10.0.0.1"]
        }
        """;

        bool ok = TelemetryPayloadParser.TryParseSystemInfo(payload, out SystemInfoFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.Hostname).IsEqualTo("web-01");
        await Assert.That(f.HardwareModel).IsEqualTo("PowerEdge R740");
        await Assert.That(f.HardwareVendor).IsEqualTo("Dell");
        await Assert.That(f.HardwareSerial).IsEqualTo("SVC123");
        await Assert.That(f.CpuBrand).IsEqualTo("Xeon");
        await Assert.That(f.CpuCores).IsEqualTo(16);
        await Assert.That(f.MemoryTotalBytes).IsEqualTo(34359738368L);
        await Assert.That(f.UptimeSeconds).IsEqualTo(1000L);
        await Assert.That(f.BiosVersion).IsEqualTo("2.1.0");
        await Assert.That(f.IpAddresses).IsNotNull();
    }

    [Test]
    public async Task TryParseSystemInfo_MissingOptionalFields_YieldsNulls()
    {
        bool ok = TelemetryPayloadParser.TryParseSystemInfo("{}", out SystemInfoFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.Hostname).IsNull();
        await Assert.That(f.CpuCores).IsNull();
        await Assert.That(f.MemoryTotalBytes).IsNull();
        await Assert.That(f.IpAddresses).IsNull();
    }

    [Test]
    public async Task TryParseSystemInfo_MalformedJson_ReturnsFalse()
    {
        bool ok = TelemetryPayloadParser.TryParseSystemInfo("not json", out SystemInfoFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseOsVersion_FullPayload_MapsAllFields()
    {
        string payload = """{ "os_name": "Ubuntu", "os_version": "22.04", "kernel": "5.15.0" }""";

        bool ok = TelemetryPayloadParser.TryParseOsVersion(payload, out OsVersionFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.OsName).IsEqualTo("Ubuntu");
        await Assert.That(f.OsVersion).IsEqualTo("22.04");
        await Assert.That(f.Kernel).IsEqualTo("5.15.0");
    }

    [Test]
    public async Task TryParseOsVersion_MissingFields_YieldsNulls()
    {
        bool ok = TelemetryPayloadParser.TryParseOsVersion("{}", out OsVersionFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.OsName).IsNull();
        await Assert.That(f.OsVersion).IsNull();
        await Assert.That(f.Kernel).IsNull();
    }

    [Test]
    public async Task TryParseOsVersion_MalformedJson_ReturnsFalse()
    {
        bool ok = TelemetryPayloadParser.TryParseOsVersion("][", out OsVersionFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseCpuInfo_FullPayload_MapsAllFields()
    {
        string payload = """{ "cpu_type": "x86_64", "physical_cpus": 2, "logical_cpus": 32 }""";

        bool ok = TelemetryPayloadParser.TryParseCpuInfo(payload, out CpuInfoFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.CpuType).IsEqualTo("x86_64");
        await Assert.That(f.CpuPhysicalCpus).IsEqualTo(2);
        await Assert.That(f.CpuLogicalCpus).IsEqualTo(32);
    }

    [Test]
    public async Task TryParseCpuInfo_MissingFields_YieldsNulls()
    {
        bool ok = TelemetryPayloadParser.TryParseCpuInfo("{}", out CpuInfoFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.CpuType).IsNull();
        await Assert.That(f.CpuPhysicalCpus).IsNull();
        await Assert.That(f.CpuLogicalCpus).IsNull();
    }

    [Test]
    public async Task TryParseCpuInfo_MalformedJson_ReturnsFalse()
    {
        bool ok = TelemetryPayloadParser.TryParseCpuInfo("garbage", out CpuInfoFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseMemoryInfo_FullPayload_MapsAllFields()
    {
        string payload = """{ "swap_total_bytes": 2147483648, "swap_free_bytes": 1073741824 }""";

        bool ok = TelemetryPayloadParser.TryParseMemoryInfo(payload, out MemoryInfoFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.SwapTotalBytes).IsEqualTo(2147483648L);
        await Assert.That(f.SwapFreeBytes).IsEqualTo(1073741824L);
    }

    [Test]
    public async Task TryParseMemoryInfo_MissingFields_YieldsNulls()
    {
        bool ok = TelemetryPayloadParser.TryParseMemoryInfo("{}", out MemoryInfoFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.SwapTotalBytes).IsNull();
        await Assert.That(f.SwapFreeBytes).IsNull();
    }

    [Test]
    public async Task TryParseMemoryInfo_MalformedJson_ReturnsFalse()
    {
        bool ok = TelemetryPayloadParser.TryParseMemoryInfo("not json", out MemoryInfoFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseDiskInfo_AnyPayload_StoresRawPayload()
    {
        string payload = """[{"name":"sda","size_bytes":500107862016}]""";

        bool ok = TelemetryPayloadParser.TryParseDiskInfo(payload, out DiskInfoFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.DiskInfos).IsEqualTo(payload);
    }

    [Test]
    public async Task TryParseCpuUsage_ReadsPercent()
    {
        bool ok = TelemetryPayloadParser.TryParseCpuUsage("""{ "cpu_usage_percent": 73 }""", out CpuUsageFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.CpuUsagePercent).IsEqualTo(73);
    }

    [Test]
    public async Task TryParseCpuUsage_MissingPercent_YieldsNull()
    {
        bool ok = TelemetryPayloadParser.TryParseCpuUsage("{}", out CpuUsageFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.CpuUsagePercent).IsNull();
    }

    [Test]
    public async Task TryParseCpuUsage_MalformedJson_ReturnsFalse()
    {
        bool ok = TelemetryPayloadParser.TryParseCpuUsage("not json", out CpuUsageFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseCpuUsage_WrongTypedField_ReturnsFalse()
    {
        // Structurally valid JSON whose numeric field is a string makes GetInt32 throw. The parser
        // must surface that as a skippable poison row (false), not let the exception escape.
        bool ok = TelemetryPayloadParser.TryParseCpuUsage("""{ "cpu_usage_percent": "x" }""", out CpuUsageFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseSystemInfo_WrongTypedIntField_ReturnsFalse()
    {
        // A string where cpu_cores expects an int makes GetInt32 throw; treat it as a poison row.
        bool ok = TelemetryPayloadParser.TryParseSystemInfo("""{ "cpu_cores": "sixteen" }""", out SystemInfoFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseMemoryUsage_FullPayload_MapsSummaryAndDetail()
    {
        string payload = """{ "memory_usage_percent": 64, "memory_used": 8589934592 }""";

        bool ok = TelemetryPayloadParser.TryParseMemoryUsage(payload, out MemoryUsageFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.MemoryUsagePercent).IsEqualTo(64);
        await Assert.That(f.MemoryUsedBytes).IsEqualTo(8589934592L);
    }

    [Test]
    public async Task TryParseMemoryUsage_MissingFields_YieldsNulls()
    {
        bool ok = TelemetryPayloadParser.TryParseMemoryUsage("{}", out MemoryUsageFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.MemoryUsagePercent).IsNull();
        await Assert.That(f.MemoryUsedBytes).IsNull();
    }

    [Test]
    public async Task TryParseMemoryUsage_MalformedJson_ReturnsFalse()
    {
        bool ok = TelemetryPayloadParser.TryParseMemoryUsage("][", out MemoryUsageFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseDiskUsage_ComputesMaxPercentAcrossAllDisks()
    {
        string payload = """
        { "disks": [ { "usage_percent": 12 }, { "usage_percent": 87 }, { "usage_percent": 40 } ] }
        """;

        bool ok = TelemetryPayloadParser.TryParseDiskUsage(payload, out DiskUsageFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.MaxDiskUsagePercent).IsEqualTo(87);
        await Assert.That(f.DiskUsages).IsEqualTo(payload);
    }

    [Test]
    public async Task TryParseDiskUsage_RootArrayPayload_ComputesMaxPercent()
    {
        string payload = """[ { "usage_percent": 5 }, { "usage_percent": 55 } ]""";

        bool ok = TelemetryPayloadParser.TryParseDiskUsage(payload, out DiskUsageFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.MaxDiskUsagePercent).IsEqualTo(55);
        await Assert.That(f.DiskUsages).IsEqualTo(payload);
    }

    [Test]
    public async Task TryParseDiskUsage_NoDisks_YieldsZeroMax()
    {
        string payload = "{}";

        bool ok = TelemetryPayloadParser.TryParseDiskUsage(payload, out DiskUsageFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.MaxDiskUsagePercent).IsEqualTo(0);
        await Assert.That(f.DiskUsages).IsEqualTo(payload);
    }

    [Test]
    public async Task TryParseHardwareHealth_FlagsDiskAndHardwareIssues()
    {
        string payload = """
        {
          "disk_smart": [ { "health_status": "FAILED" } ],
          "fans": [ { "rpm": 0 } ]
        }
        """;

        bool ok = TelemetryPayloadParser.TryParseHardwareHealth(payload, out HardwareHealthFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.HasDiskHealthIssue).IsTrue();
        await Assert.That(f.HasHardwareIssue).IsTrue();
        await Assert.That(f.HardwareHealth).IsEqualTo(payload);
    }

    [Test]
    public async Task TryParseHardwareHealth_FailingPowerSupply_FlagsHardwareIssue()
    {
        string payload = """{ "power_supplies": [ { "status": "FAILED" } ] }""";

        bool ok = TelemetryPayloadParser.TryParseHardwareHealth(payload, out HardwareHealthFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.HasDiskHealthIssue).IsFalse();
        await Assert.That(f.HasHardwareIssue).IsTrue();
    }

    [Test]
    public async Task TryParseHardwareHealth_AllHealthy_FlagsNothing()
    {
        string payload = """
        {
          "disk_smart": [ { "health_status": "PASSED" } ],
          "fans": [ { "rpm": 3200 } ],
          "power_supplies": [ { "status": "OK" } ]
        }
        """;

        bool ok = TelemetryPayloadParser.TryParseHardwareHealth(payload, out HardwareHealthFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.HasDiskHealthIssue).IsFalse();
        await Assert.That(f.HasHardwareIssue).IsFalse();
        await Assert.That(f.HardwareHealth).IsEqualTo(payload);
    }

    [Test]
    public async Task TryParseHardwareHealth_MalformedJson_ReturnsFalse()
    {
        bool ok = TelemetryPayloadParser.TryParseHardwareHealth("not json", out HardwareHealthFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseSshSessions_AnyPayload_StoresRawPayload()
    {
        string payload = """[{"user":"root","from":"10.0.0.5"}]""";

        bool ok = TelemetryPayloadParser.TryParseSshSessions(payload, out SshSessionsFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.SshSessions).IsEqualTo(payload);
    }

    [Test]
    public async Task TryParsePackageUpdates_FullPayload_MapsAllFields()
    {
        string payload = """{ "pending_updates": 12, "security_updates": 3 }""";

        bool ok = TelemetryPayloadParser.TryParsePackageUpdates(payload, out PackageUpdatesFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.PendingUpdates).IsEqualTo(12);
        await Assert.That(f.SecurityUpdates).IsEqualTo(3);
    }

    [Test]
    public async Task TryParsePackageUpdates_MissingFields_YieldsNulls()
    {
        bool ok = TelemetryPayloadParser.TryParsePackageUpdates("{}", out PackageUpdatesFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.PendingUpdates).IsNull();
        await Assert.That(f.SecurityUpdates).IsNull();
    }

    [Test]
    public async Task TryParsePackageUpdates_MalformedJson_ReturnsFalse()
    {
        bool ok = TelemetryPayloadParser.TryParsePackageUpdates("][", out PackageUpdatesFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseServiceStatus_FullPayload_MapsAllFields()
    {
        string payload = """{ "total_services": 120, "failed_services": 2 }""";

        bool ok = TelemetryPayloadParser.TryParseServiceStatus(payload, out ServiceStatusFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.TotalServices).IsEqualTo(120);
        await Assert.That(f.FailedServices).IsEqualTo(2);
    }

    [Test]
    public async Task TryParseServiceStatus_MissingFields_YieldsNulls()
    {
        bool ok = TelemetryPayloadParser.TryParseServiceStatus("{}", out ServiceStatusFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.TotalServices).IsNull();
        await Assert.That(f.FailedServices).IsNull();
    }

    [Test]
    public async Task TryParseServiceStatus_MalformedJson_ReturnsFalse()
    {
        bool ok = TelemetryPayloadParser.TryParseServiceStatus("not json", out ServiceStatusFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task ComputeMaxDiskUsagePercent_WrappedDisks_ReturnsMax()
    {
        string payload = """{ "disks": [ { "usage_percent": 30 }, { "usage_percent": 92 } ] }""";

        int max = TelemetryPayloadParser.ComputeMaxDiskUsagePercent(payload);

        await Assert.That(max).IsEqualTo(92);
    }

    [Test]
    public async Task ComputeMaxDiskUsagePercent_MalformedJson_ReturnsZero()
    {
        int max = TelemetryPayloadParser.ComputeMaxDiskUsagePercent("not json");

        await Assert.That(max).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_FailedDiskSmart_FlagsDiskIssue()
    {
        string payload = """{ "disk_smart": [ { "health_status": "FAILED" } ] }""";

        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(payload);

        await Assert.That(hasDiskIssue).IsTrue();
        await Assert.That(hasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_StoppedFan_FlagsHardwareIssue()
    {
        string payload = """{ "fans": [ { "rpm": 0 } ] }""";

        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(payload);

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsTrue();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_MalformedJson_ReturnsFalseFlags()
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags("not json");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsFalse();
    }
}
