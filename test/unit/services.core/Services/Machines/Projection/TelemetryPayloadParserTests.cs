// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Grpc.AgentTelemetry;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines.Projection;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Services.Machines.Projection;

/// <summary>
/// Tests for <see cref="TelemetryPayloadParser"/>. These assert the documented projection
/// values for each telemetry type, malformed-payload handling, and the two computed columns.
/// </summary>
/// <remarks>
/// Every well-formed fixture is produced by serializing the actual protobuf record with the same
/// options the ingest path uses, so the test cannot agree with the parser on a field name that the
/// server never writes. Hand-written JSON appears only where the shape under test is deliberately
/// not a valid payload (malformed text, wrong-typed fields, legacy shapes).
/// </remarks>
public class TelemetryPayloadParserTests
{
    [Test]
    public async Task TryParseSystemInfo_FullPayload_MapsEverySummaryAndDetailField()
    {
        string payload = Serialize(new SystemInfoRecord
        {
            Hostname = "web-01",
            HardwareModel = "PowerEdge R740",
            HardwareVendor = "Dell",
            HardwareSerial = "SVC123",
            CpuBrand = "Xeon",
            CpuPhysicalCores = 16,
            PhysicalMemory = 34359738368,
            UptimeSeconds = 1000,
            BiosVersion = "2.1.0",
            IpAddresses = { "10.0.0.1" }
        });

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
        // The agent reports the running kernel in the record's build field; the message has no
        // dedicated kernel field, so build is the only source for the projected Kernel column.
        string payload = Serialize(new OsVersionRecord
        {
            Name = "Ubuntu",
            Version = "22.04",
            Build = "5.15.0-91-generic",
            Platform = "ubuntu"
        });

        bool ok = TelemetryPayloadParser.TryParseOsVersion(payload, out OsVersionFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.OsName).IsEqualTo("Ubuntu");
        await Assert.That(f.OsVersion).IsEqualTo("22.04");
        await Assert.That(f.Kernel).IsEqualTo("5.15.0-91-generic");
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
    public async Task TryParseAgentVersion_PayloadWithVersion_MapsVersion()
    {
        string payload = Serialize(new AgentVersionRecord { Version = "1.16.0" });

        bool ok = TelemetryPayloadParser.TryParseAgentVersion(payload, out AgentVersionFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.AgentVersion).IsEqualTo("1.16.0");
    }

    [Test]
    public async Task TryParseAgentVersion_MissingVersion_ReturnsFalseSoTheRecordedVersionSurvives()
    {
        bool ok = TelemetryPayloadParser.TryParseAgentVersion("{}", out AgentVersionFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseAgentVersion_BlankVersion_ReturnsFalseSoTheRecordedVersionSurvives()
    {
        foreach (string payload in new[] { """{ "version": "" }""", """{ "version": "   " }""", """{ "version": null }""" })
        {
            bool ok = TelemetryPayloadParser.TryParseAgentVersion(payload, out AgentVersionFragment? f);

            await Assert.That(ok).IsFalse();
            await Assert.That(f).IsNull();
        }
    }

    [Test]
    public async Task TryParseAgentVersion_MalformedJson_ReturnsFalse()
    {
        bool ok = TelemetryPayloadParser.TryParseAgentVersion("][", out AgentVersionFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseAgentVersion_WrongTypedVersion_ReturnsFalse()
    {
        // A number where the version string is expected makes GetString throw; skip the poison row.
        bool ok = TelemetryPayloadParser.TryParseAgentVersion("""{ "version": 116 }""", out AgentVersionFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseCpuInfo_FullPayload_MapsAllFields()
    {
        string payload = Serialize(new CpuInfoRecord
        {
            ProcessorType = "x86_64",
            NumberOfCores = "2",
            LogicalProcessors = 32
        });

        bool ok = TelemetryPayloadParser.TryParseCpuInfo(payload, out CpuInfoFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.CpuType).IsEqualTo("x86_64");
        await Assert.That(f.CpuPhysicalCpus).IsEqualTo(2);
        await Assert.That(f.CpuLogicalCpus).IsEqualTo(32);
    }

    [Test]
    public async Task TryParseCpuInfo_NonNumericCoreCount_YieldsNullWithoutSkippingTheRow()
    {
        // number_of_cores is a proto string, so non-numeric text is a well-typed payload. The row must
        // still project its other CPU columns rather than being discarded as poison.
        string payload = Serialize(new CpuInfoRecord
        {
            ProcessorType = "aarch64",
            NumberOfCores = "unknown",
            LogicalProcessors = 8
        });

        bool ok = TelemetryPayloadParser.TryParseCpuInfo(payload, out CpuInfoFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.CpuType).IsEqualTo("aarch64");
        await Assert.That(f.CpuPhysicalCpus).IsNull();
        await Assert.That(f.CpuLogicalCpus).IsEqualTo(8);
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
        string payload = Serialize(new MemoryInfoRecord
        {
            MemoryTotal = 34359738368,
            SwapTotal = 2147483648,
            SwapFree = 1073741824
        });

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
        string payload = Serialize(new DiskInfoRecord
        {
            Disks = { new DiskInfoEntry { Device = "sda", MountPoint = "/", TotalBytes = 500107862016 } }
        });

        bool ok = TelemetryPayloadParser.TryParseDiskInfo(payload, out DiskInfoFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.DiskInfos).IsEqualTo(payload);
    }

    [Test]
    public async Task TryParseCpuUsage_ReadsPercent()
    {
        string payload = Serialize(new CpuUtilizationRecord { CpuUsagePercent = 73 });

        bool ok = TelemetryPayloadParser.TryParseCpuUsage(payload, out CpuUsageFragment? f);

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
        // A string where cpu_physical_cores expects an int makes GetInt32 throw; treat it as a poison row.
        bool ok = TelemetryPayloadParser.TryParseSystemInfo("""{ "cpu_physical_cores": "sixteen" }""", out SystemInfoFragment? f);

        await Assert.That(ok).IsFalse();
        await Assert.That(f).IsNull();
    }

    [Test]
    public async Task TryParseMemoryUsage_FullPayload_MapsSummaryAndDetail()
    {
        string payload = Serialize(new MemoryUtilizationRecord
        {
            MemoryTotal = 16000000000,
            MemoryUsagePercent = 64,
            MemoryUsed = 8589934592
        });

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
        string payload = Serialize(new DiskUtilizationRecord
        {
            Disks =
            {
                new DiskUtilizationEntry { Path = "/", UsagePercent = 12 },
                new DiskUtilizationEntry { Path = "/data", UsagePercent = 87 },
                new DiskUtilizationEntry { Path = "/var", UsagePercent = 40 }
            }
        });

        bool ok = TelemetryPayloadParser.TryParseDiskUsage(payload, out DiskUsageFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.MaxDiskUsagePercent).IsEqualTo(87);
        await Assert.That(f.DiskUsages).IsEqualTo(payload);
    }

    [Test]
    public async Task TryParseDiskUsage_RootArrayPayload_ComputesMaxPercent()
    {
        // A bare array is not what the current agent sends; the parser accepts it so historical rows
        // stored in that shape still project. Written by hand for exactly that reason.
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
        string payload = Serialize(new HardwareHealthRecord
        {
            DiskSmart = { new DiskSmartReading { Device = "/dev/sda", HealthStatus = "FAILED" } },
            Fans = { new FanReading { Name = "fan1", Rpm = 0 } }
        });

        bool ok = TelemetryPayloadParser.TryParseHardwareHealth(payload, out HardwareHealthFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.HasDiskHealthIssue).IsTrue();
        await Assert.That(f.HasHardwareIssue).IsTrue();
        await Assert.That(f.HardwareHealth).IsEqualTo(payload);
    }

    [Test]
    public async Task TryParseHardwareHealth_FailingPowerSupply_FlagsHardwareIssue()
    {
        string payload = Serialize(new HardwareHealthRecord
        {
            PowerSupplies = { new PowerSupplyReading { Name = "psu1", Status = "FAILED" } }
        });

        bool ok = TelemetryPayloadParser.TryParseHardwareHealth(payload, out HardwareHealthFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.HasDiskHealthIssue).IsFalse();
        await Assert.That(f.HasHardwareIssue).IsTrue();
    }

    [Test]
    public async Task TryParseHardwareHealth_AllHealthy_FlagsNothing()
    {
        string payload = Serialize(new HardwareHealthRecord
        {
            DiskSmart = { new DiskSmartReading { Device = "/dev/sda", HealthStatus = "PASSED" } },
            Fans = { new FanReading { Name = "fan1", Rpm = 3200 } },
            PowerSupplies = { new PowerSupplyReading { Name = "psu1", Status = "OK" } }
        });

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
        string payload = Serialize(new SshSessionRecord
        {
            User = "root",
            SourceIp = "10.0.0.5",
            Action = "connect"
        });

        bool ok = TelemetryPayloadParser.TryParseSshSessions(payload, out SshSessionsFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.SshSessions).IsEqualTo(payload);
    }

    [Test]
    public async Task TryParsePackageUpdates_CountsPendingAndSecurityUpdates()
    {
        // PackageUpdatesRecord carries only the update list; both projected counts are derived from it.
        string payload = Serialize(new PackageUpdatesRecord
        {
            PackageManager = "apt",
            Updates =
            {
                new PackageUpdate { Name = "openssl", IsSecurityUpdate = true },
                new PackageUpdate { Name = "curl", IsSecurityUpdate = true },
                new PackageUpdate { Name = "vim", IsSecurityUpdate = false }
            }
        });

        bool ok = TelemetryPayloadParser.TryParsePackageUpdates(payload, out PackageUpdatesFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.PendingUpdates).IsEqualTo(3);
        await Assert.That(f.SecurityUpdates).IsEqualTo(2);
    }

    [Test]
    public async Task TryParsePackageUpdates_NoUpdatesReported_YieldsZeroCounts()
    {
        // An up-to-date machine sends an empty list. That is "zero pending", not "unknown".
        string payload = Serialize(new PackageUpdatesRecord { PackageManager = "apt" });

        bool ok = TelemetryPayloadParser.TryParsePackageUpdates(payload, out PackageUpdatesFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.PendingUpdates).IsEqualTo(0);
        await Assert.That(f.SecurityUpdates).IsEqualTo(0);
    }

    [Test]
    public async Task TryParsePackageUpdates_MissingUpdatesArray_YieldsNulls()
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
    public async Task TryParseServiceStatus_CountsTotalAndFailedServices()
    {
        // ServiceStatusRecord carries only the unit list; both projected counts are derived from it,
        // with "failed" defined by the systemd active state exactly as the history read path does.
        string payload = Serialize(new ServiceStatusRecord
        {
            Services =
            {
                new ServiceEntry { Unit = "ssh.service", ActiveState = "active" },
                new ServiceEntry { Unit = "nginx.service", ActiveState = "failed" },
                new ServiceEntry { Unit = "cron.service", ActiveState = "active" },
                new ServiceEntry { Unit = "postgres.service", ActiveState = "Failed" }
            }
        });

        bool ok = TelemetryPayloadParser.TryParseServiceStatus(payload, out ServiceStatusFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.TotalServices).IsEqualTo(4);
        await Assert.That(f.FailedServices).IsEqualTo(2);
    }

    [Test]
    public async Task TryParseServiceStatus_AllServicesHealthy_YieldsZeroFailed()
    {
        string payload = Serialize(new ServiceStatusRecord
        {
            Services =
            {
                new ServiceEntry { Unit = "ssh.service", ActiveState = "active" },
                new ServiceEntry { Unit = "cron.service", ActiveState = "inactive" }
            }
        });

        bool ok = TelemetryPayloadParser.TryParseServiceStatus(payload, out ServiceStatusFragment? f);

        await Assert.That(ok).IsTrue();
        await Assert.That(f!.TotalServices).IsEqualTo(2);
        await Assert.That(f.FailedServices).IsEqualTo(0);
    }

    [Test]
    public async Task TryParseServiceStatus_MissingServicesArray_YieldsNulls()
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
        string payload = Serialize(new DiskUtilizationRecord
        {
            Disks =
            {
                new DiskUtilizationEntry { Path = "/", UsagePercent = 30 },
                new DiskUtilizationEntry { Path = "/data", UsagePercent = 92 }
            }
        });

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
        string payload = Serialize(new HardwareHealthRecord
        {
            DiskSmart = { new DiskSmartReading { Device = "/dev/sda", HealthStatus = "FAILED" } }
        });

        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(payload);

        await Assert.That(hasDiskIssue).IsTrue();
        await Assert.That(hasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_StoppedFan_FlagsHardwareIssue()
    {
        string payload = Serialize(new HardwareHealthRecord
        {
            Fans = { new FanReading { Name = "fan1", Rpm = 0 } }
        });

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

    /// <summary>
    /// Serializes a protobuf telemetry record the same way the ingest path stores it, so a fixture can
    /// only ever carry the field names the server actually writes.
    /// </summary>
    /// <param name="record">The protobuf telemetry record to serialize.</param>
    /// <returns>The stored payload JSON for that record.</returns>
    private static string Serialize(object record) =>
        JsonSerializer.Serialize(record, JsonDefaults.SnakeCase);
}
