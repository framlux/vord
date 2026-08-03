// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.FleetManagement.Grpc.AgentTelemetry;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Machines.Projection;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Grpc.Core;
using Grpc.Net.Client;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Grpc;

/// <summary>
/// End-to-end pipeline tests that submit telemetry via gRPC (proto serialization)
/// and verify the streaming projection correctly deserializes and updates state tables.
/// Guards against field name mismatches between the write path (TelemetryService.SerializePayload)
/// and the read path (TelemetryPayloadParser via MachineStateBatchCollapser).
/// </summary>
public sealed class TelemetryPipelineTests
{
    [Test]
    public async Task Pipeline_CpuUtilization_RoundTripsToStateTables()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-cpu-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 73 }
                }
            }
        };

        // Act — submit via gRPC (proto binary on the wire, stored as JSON in DB)
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        // Read back the persisted row and process it through the streaming service
        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        await ProjectRowsAsync(db, row!);

        // Assert — the CPU value must survive the full proto → JSON → parse round-trip
        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.CpuUsagePercent).IsEqualTo(73);
    }

    [Test]
    public async Task Pipeline_MemoryUtilization_RoundTripsToStateTables()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-mem-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.MemoryUtilizationType,
                    MemoryUtilization = new MemoryUtilizationRecord
                    {
                        MemoryTotal = 16_000_000_000,
                        MemoryFree = 4_000_000_000,
                        MemoryUsed = 12_000_000_000,
                        MemoryUsagePercent = 75
                    }
                }
            }
        };

        // Act
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        await ProjectRowsAsync(db, row!);

        // Assert
        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.MemoryUsagePercent).IsEqualTo(75);

        MachineStateDetail? detail = await db.MachineStateDetails
            .FirstOrDefaultAsync(d => d.MachineId == machineId);
        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.MemoryUsedBytes).IsEqualTo(12_000_000_000);
    }

    [Test]
    public async Task Pipeline_DiskUtilization_RoundTripsToStateTables()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-disk-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.DiskUtilizationType,
                    DiskUtilization = new DiskUtilizationRecord
                    {
                        Disks =
                        {
                            new DiskUtilizationEntry
                            {
                                Device = "/dev/sda1",
                                Path = "/",
                                Blocks = 500_000_000,
                                BlocksFree = 150_000_000,
                                BlocksUsed = 350_000_000,
                                UsagePercent = 70
                            },
                            new DiskUtilizationEntry
                            {
                                Device = "/dev/sdb1",
                                Path = "/data",
                                Blocks = 1_000_000_000,
                                BlocksFree = 100_000_000,
                                BlocksUsed = 900_000_000,
                                UsagePercent = 90
                            }
                        }
                    }
                }
            }
        };

        // Act
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        await ProjectRowsAsync(db, row!);

        // Assert — max disk usage should be the highest across all disks (90%)
        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.MaxDiskUsagePercent).IsEqualTo(90);
    }

    [Test]
    public async Task Pipeline_AllFastTelemetry_RoundTripsCorrectly()
    {
        // Arrange — submit all fast-tick telemetry types in one envelope and verify state
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-all-fast-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string cpuEventId = Guid.NewGuid().ToString("N");
        string memEventId = Guid.NewGuid().ToString("N");
        string diskEventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = cpuEventId,
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 55 }
                },
                new TelemetryItem
                {
                    EventId = memEventId,
                    Type = TelemetryTypes.MemoryUtilizationType,
                    MemoryUtilization = new MemoryUtilizationRecord
                    {
                        MemoryTotal = 32_000_000_000,
                        MemoryUsed = 24_000_000_000,
                        MemoryUsagePercent = 75
                    }
                },
                new TelemetryItem
                {
                    EventId = diskEventId,
                    Type = TelemetryTypes.DiskUtilizationType,
                    DiskUtilization = new DiskUtilizationRecord
                    {
                        Disks = { new DiskUtilizationEntry { Path = "/", UsagePercent = 63 } }
                    }
                }
            }
        };

        // Act
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        // Project all persisted rows through the production collapse-and-apply path in one batch.
        List<MachineTelemetry> rows = await db.MachineTelemetry
            .Where(t => t.MachineId == machineId)
            .OrderBy(t => t.Id)
            .ToListAsync();

        await ProjectRowsAsync(db, rows.ToArray());

        // Assert all values round-tripped correctly
        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.CpuUsagePercent).IsEqualTo(55);
        await Assert.That(summary!.MemoryUsagePercent).IsEqualTo(75);
        await Assert.That(summary!.MaxDiskUsagePercent).IsEqualTo(63);

        MachineStateDetail? detail = await db.MachineStateDetails
            .FirstOrDefaultAsync(d => d.MachineId == machineId);
        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.MemoryUsedBytes).IsEqualTo(24_000_000_000);
    }

    [Test]
    public async Task Pipeline_AgentVersion_RoundTripsToTelemetryHistoryAndDetailTable()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-agent-version-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.AgentVersionType,
                    AgentVersion = new AgentVersionRecord { Version = "1.16.0" }
                }
            }
        };

        // Act
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        // The version is retained as history in MachineTelemetry, stamped with the agent version type.
        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.TelemetryType).IsEqualTo(TelemetryTypeIds.AgentVersion);
        await Assert.That(row.Payload).Contains("1.16.0");

        await ProjectRowsAsync(db, row);

        // Assert — the current value lands on the machine's detail row.
        MachineStateDetail? detail = await db.MachineStateDetails
            .FirstOrDefaultAsync(d => d.MachineId == machineId);
        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.AgentVersion).IsEqualTo("1.16.0");
    }

    [Test]
    public async Task Pipeline_AgentVersionWithoutVersion_LeavesTheRecordedVersionInPlace()
    {
        // Intent: an agent that reports no version must not wipe the version already recorded, so
        // support keeps the last known version instead of seeing it disappear.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-agent-version-empty-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);
        await db.GetTable<MachineStateDetail>().Where(d => d.MachineId == machineId)
            .Set(d => d.AgentVersion, "1.15.3").UpdateAsync();

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.AgentVersionType,
                    AgentVersion = new AgentVersionRecord()
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        await ProjectRowsAsync(db, row!);

        MachineStateDetail? detail = await db.MachineStateDetails
            .FirstOrDefaultAsync(d => d.MachineId == machineId);
        await Assert.That(detail!.AgentVersion).IsEqualTo("1.15.3");
    }

    [Test]
    public async Task Pipeline_SystemInfo_RoundTripsCoreCountAndMemoryToStateTables()
    {
        // Intent: the inventory columns the fleet list and machine detail page read must survive the
        // proto to stored-JSON to parser round-trip. Core count and total memory are the two that a
        // name-only fixture cannot protect, because the proto calls them cpu_physical_cores and
        // physical_memory rather than anything the projection column names suggest.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-system-info-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.SystemInfoType,
                    SystemInfo = new SystemInfoRecord
                    {
                        Hostname = "web-01",
                        HardwareModel = "PowerEdge R740",
                        HardwareVendor = "Dell",
                        HardwareSerial = "SVC123",
                        CpuBrand = "Xeon Gold 6248",
                        CpuPhysicalCores = 16,
                        CpuLogicalCores = 32,
                        PhysicalMemory = 34_359_738_368,
                        UptimeSeconds = 86_400,
                        BiosVersion = "2.1.0",
                        IpAddresses = { "10.0.0.1" }
                    }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        await ProjectRowsAsync(db, row!);

        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.Hostname).IsEqualTo("web-01");
        await Assert.That(summary.HardwareModel).IsEqualTo("PowerEdge R740");
        await Assert.That(summary.IpAddresses).IsNotNull();

        MachineStateDetail? detail = await db.MachineStateDetails
            .FirstOrDefaultAsync(d => d.MachineId == machineId);
        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.HardwareVendor).IsEqualTo("Dell");
        await Assert.That(detail.HardwareSerial).IsEqualTo("SVC123");
        await Assert.That(detail.CpuBrand).IsEqualTo("Xeon Gold 6248");
        await Assert.That(detail.CpuCores).IsEqualTo(16);
        await Assert.That(detail.MemoryTotalBytes).IsEqualTo(34_359_738_368L);
        await Assert.That(detail.UptimeSeconds).IsEqualTo(86_400L);
        await Assert.That(detail.BiosVersion).IsEqualTo("2.1.0");
    }

    [Test]
    public async Task Pipeline_OsVersion_RoundTripsNameVersionAndKernelToStateTables()
    {
        // Intent: OsName and OsVersion feed the fleet list, and the kernel string the agent reports in
        // the record's build field feeds the machine detail page.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-os-version-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.OsVersionType,
                    OsVersion = new OsVersionRecord
                    {
                        Name = "Ubuntu",
                        Version = "22.04",
                        Major = 22,
                        Minor = 4,
                        Build = "5.15.0-91-generic",
                        Platform = "ubuntu",
                        Codename = "jammy"
                    }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        await ProjectRowsAsync(db, row!);

        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.OsName).IsEqualTo("Ubuntu");
        await Assert.That(summary.OsVersion).IsEqualTo("22.04");

        MachineStateDetail? detail = await db.MachineStateDetails
            .FirstOrDefaultAsync(d => d.MachineId == machineId);
        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.Kernel).IsEqualTo("5.15.0-91-generic");
    }

    [Test]
    public async Task Pipeline_CpuInfo_RoundTripsProcessorTypeAndCoreCountsToDetailTable()
    {
        // Intent: the physical core count is declared as a string on CpuInfoRecord, so the projection
        // must convert it rather than read it as a number and discard the whole row.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-cpu-info-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.CpuInfoType,
                    CpuInfo = new CpuInfoRecord
                    {
                        DeviceId = "CPU0",
                        Model = "Intel(R) Xeon(R) Gold 6248",
                        ProcessorType = "x86_64",
                        NumberOfCores = "2",
                        LogicalProcessors = 8
                    }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        await ProjectRowsAsync(db, row!);

        MachineStateDetail? detail = await db.MachineStateDetails
            .FirstOrDefaultAsync(d => d.MachineId == machineId);
        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.CpuType).IsEqualTo("x86_64");
        await Assert.That(detail.CpuPhysicalCpus).IsEqualTo(2);
        await Assert.That(detail.CpuLogicalCpus).IsEqualTo(8);
    }

    [Test]
    public async Task Pipeline_MemoryInfo_RoundTripsSwapTotalsToDetailTable()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-memory-info-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.MemoryInfoType,
                    MemoryInfo = new MemoryInfoRecord
                    {
                        MemoryTotal = 34_359_738_368,
                        MemoryFree = 8_589_934_592,
                        MemoryAvailable = 12_884_901_888,
                        SwapTotal = 8_589_934_592,
                        SwapFree = 4_294_967_296
                    }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        await ProjectRowsAsync(db, row!);

        MachineStateDetail? detail = await db.MachineStateDetails
            .FirstOrDefaultAsync(d => d.MachineId == machineId);
        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.SwapTotalBytes).IsEqualTo(8_589_934_592L);
        await Assert.That(detail.SwapFreeBytes).IsEqualTo(4_294_967_296L);
    }

    [Test]
    public async Task Pipeline_PackageUpdates_CountsPendingAndSecurityUpdatesIntoSummary()
    {
        // Intent: PackageUpdatesRecord carries only the update list, so the fleet list's pending and
        // security counters are derived from it during projection.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-package-updates-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.PackageUpdatesType,
                    PackageUpdates = new PackageUpdatesRecord
                    {
                        PackageManager = "apt",
                        Updates =
                        {
                            new PackageUpdate { Name = "openssl", CurrentVersion = "3.0.2", AvailableVersion = "3.0.13", IsSecurityUpdate = true },
                            new PackageUpdate { Name = "curl", CurrentVersion = "7.81.0", AvailableVersion = "7.81.1", IsSecurityUpdate = true },
                            new PackageUpdate { Name = "vim", CurrentVersion = "8.2", AvailableVersion = "8.3", IsSecurityUpdate = false }
                        }
                    }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        await ProjectRowsAsync(db, row!);

        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.PendingUpdates).IsEqualTo(3);
        await Assert.That(summary.SecurityUpdates).IsEqualTo(2);
    }

    [Test]
    public async Task Pipeline_ServiceStatus_CountsTotalAndFailedServicesIntoSummary()
    {
        // Intent: ServiceStatusRecord carries only the unit list, so the fleet list's total and failed
        // service counters are derived from it, with "failed" read from the systemd active state.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-service-status-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.ServiceStatusType,
                    ServiceStatus = new ServiceStatusRecord
                    {
                        Services =
                        {
                            new ServiceEntry { Unit = "ssh.service", LoadState = "loaded", ActiveState = "active", SubState = "running" },
                            new ServiceEntry { Unit = "nginx.service", LoadState = "loaded", ActiveState = "failed", SubState = "failed" },
                            new ServiceEntry { Unit = "cron.service", LoadState = "loaded", ActiveState = "active", SubState = "running" },
                            new ServiceEntry { Unit = "postgresql.service", LoadState = "loaded", ActiveState = "inactive", SubState = "dead" }
                        }
                    }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        await ProjectRowsAsync(db, row!);

        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.TotalServices).IsEqualTo(4);
        await Assert.That(summary.FailedServices).IsEqualTo(1);
    }

    [Test]
    public async Task Pipeline_HardwareHealth_RoundTripsIssueFlagsToSummary()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-hardware-health-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.HardwareHealthType,
                    HardwareHealth = new HardwareHealthRecord
                    {
                        DiskSmart = { new DiskSmartReading { Device = "/dev/sda", Model = "Samsung", HealthStatus = "FAILED" } },
                        Fans = { new FanReading { Name = "fan1", Rpm = 0, Status = "critical" } },
                        PowerSupplies = { new PowerSupplyReading { Name = "psu1", Watts = 750, Status = "OK" } }
                    }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        await ProjectRowsAsync(db, row!);

        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.HasDiskHealthIssue).IsTrue();
        await Assert.That(summary.HasHardwareIssue).IsTrue();
    }

    [Test]
    public async Task Pipeline_AllInventoryAndUsageTelemetry_PopulatesEveryMappedStateColumn()
    {
        // Intent: one submission carrying every telemetry type, asserted column by column. This is the
        // guard against a projection key drifting away from the proto field the server actually writes:
        // a stale key resolves to null here rather than agreeing with a hand-written fixture.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-every-column-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.SystemInfoType,
                    SystemInfo = new SystemInfoRecord
                    {
                        Hostname = "db-07",
                        HardwareModel = "ProLiant DL380",
                        HardwareVendor = "HPE",
                        HardwareSerial = "HP-99",
                        CpuBrand = "EPYC 7443",
                        CpuPhysicalCores = 24,
                        PhysicalMemory = 68_719_476_736,
                        UptimeSeconds = 172_800,
                        BiosVersion = "U30",
                        IpAddresses = { "10.1.2.3" }
                    }
                },
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.OsVersionType,
                    OsVersion = new OsVersionRecord { Name = "Debian GNU/Linux", Version = "12", Build = "6.1.0-18-amd64" }
                },
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.CpuInfoType,
                    CpuInfo = new CpuInfoRecord { ProcessorType = "amd64", NumberOfCores = "24", LogicalProcessors = 48 }
                },
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.MemoryInfoType,
                    MemoryInfo = new MemoryInfoRecord { SwapTotal = 17_179_869_184, SwapFree = 17_179_869_184 }
                },
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.DiskInfoType,
                    DiskInfo = new DiskInfoRecord
                    {
                        Disks = { new DiskInfoEntry { Device = "/dev/sda1", MountPoint = "/", FsType = "ext4", TotalBytes = 500_107_862_016 } }
                    }
                },
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 31 }
                },
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.MemoryUtilizationType,
                    MemoryUtilization = new MemoryUtilizationRecord { MemoryUsagePercent = 42, MemoryUsed = 28_991_029_248 }
                },
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.DiskUtilizationType,
                    DiskUtilization = new DiskUtilizationRecord
                    {
                        Disks = { new DiskUtilizationEntry { Path = "/", UsagePercent = 51 } }
                    }
                },
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.PackageUpdatesType,
                    PackageUpdates = new PackageUpdatesRecord
                    {
                        PackageManager = "apt",
                        Updates =
                        {
                            new PackageUpdate { Name = "openssl", IsSecurityUpdate = true },
                            new PackageUpdate { Name = "vim", IsSecurityUpdate = false }
                        }
                    }
                },
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.ServiceStatusType,
                    ServiceStatus = new ServiceStatusRecord
                    {
                        Services =
                        {
                            new ServiceEntry { Unit = "ssh.service", ActiveState = "active" },
                            new ServiceEntry { Unit = "nginx.service", ActiveState = "failed" }
                        }
                    }
                },
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.AgentVersionType,
                    AgentVersion = new AgentVersionRecord { Version = "1.16.0" }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        List<MachineTelemetry> rows = await db.MachineTelemetry
            .Where(t => t.MachineId == machineId)
            .OrderBy(t => t.Id)
            .ToListAsync();

        await ProjectRowsAsync(db, rows.ToArray());

        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.Hostname).IsEqualTo("db-07");
        await Assert.That(summary.HardwareModel).IsEqualTo("ProLiant DL380");
        await Assert.That(summary.IpAddresses).IsNotNull();
        await Assert.That(summary.OsName).IsEqualTo("Debian GNU/Linux");
        await Assert.That(summary.OsVersion).IsEqualTo("12");
        await Assert.That(summary.CpuUsagePercent).IsEqualTo(31);
        await Assert.That(summary.MemoryUsagePercent).IsEqualTo(42);
        await Assert.That(summary.MaxDiskUsagePercent).IsEqualTo(51);
        await Assert.That(summary.PendingUpdates).IsEqualTo(2);
        await Assert.That(summary.SecurityUpdates).IsEqualTo(1);
        await Assert.That(summary.TotalServices).IsEqualTo(2);
        await Assert.That(summary.FailedServices).IsEqualTo(1);

        MachineStateDetail? detail = await db.MachineStateDetails
            .FirstOrDefaultAsync(d => d.MachineId == machineId);
        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.HardwareVendor).IsEqualTo("HPE");
        await Assert.That(detail.HardwareSerial).IsEqualTo("HP-99");
        await Assert.That(detail.CpuBrand).IsEqualTo("EPYC 7443");
        await Assert.That(detail.CpuCores).IsEqualTo(24);
        await Assert.That(detail.MemoryTotalBytes).IsEqualTo(68_719_476_736L);
        await Assert.That(detail.UptimeSeconds).IsEqualTo(172_800L);
        await Assert.That(detail.BiosVersion).IsEqualTo("U30");
        await Assert.That(detail.Kernel).IsEqualTo("6.1.0-18-amd64");
        await Assert.That(detail.CpuType).IsEqualTo("amd64");
        await Assert.That(detail.CpuPhysicalCpus).IsEqualTo(24);
        await Assert.That(detail.CpuLogicalCpus).IsEqualTo(48);
        await Assert.That(detail.SwapTotalBytes).IsEqualTo(17_179_869_184L);
        await Assert.That(detail.SwapFreeBytes).IsEqualTo(17_179_869_184L);
        await Assert.That(detail.MemoryUsedBytes).IsEqualTo(28_991_029_248L);
        await Assert.That(detail.DiskInfos).IsNotNull();
        await Assert.That(detail.DiskUsages).IsNotNull();
        await Assert.That(detail.AgentVersion).IsEqualTo("1.16.0");
    }

    /// <summary>
    /// Projects the supplied raw telemetry rows into the state tables exactly as the streaming
    /// service does: collapse the rows to one patch per machine via the production collapser, then
    /// apply the combined summary and detail patches through the public repository methods.
    /// </summary>
    private static async Task ProjectRowsAsync(DatabaseContext db, params MachineTelemetry[] rows)
    {
        IMachineStateRepository repo = new DatabaseRepository(db, NullLogger<DatabaseRepository>.Instance);
        CollapseResult collapse = MachineStateBatchCollapser.Collapse(rows);

        foreach (MachineStatePatch patch in collapse.Patches)
        {
            await repo.ApplySummaryPatchAsync(MachineStateStreamingService.MapSummary(patch), CancellationToken.None);

            if (patch.HasDetailChanges == true)
            {
                await repo.ApplyDetailPatchAsync(MachineStateStreamingService.MapDetail(patch), CancellationToken.None);
            }
        }
    }

    private static GrpcChannel CreateChannel(FunctionalTestFactory factory)
    {
        HttpMessageHandler handler = new ResponseVersionHandler
        {
            InnerHandler = factory.Server.CreateHandler()
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });
    }

    private static async Task<(long machineId, int tenantId)> SeedMachineWithStateRows(
        DatabaseContext db,
        string plaintextApiKey)
    {
        Tenant tenant = new()
        {
            Name = $"Pipeline Test Tenant {Guid.NewGuid():N}",
            ExternalId = $"ext-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        int tenantId = (int)(long)await db.InsertWithIdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(subscription);

        string apiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextApiKey)));
        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Pipeline Test Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false
        };
        long tokenId = (long)await db.InsertWithIdentityAsync(token);

        Machine machine = new()
        {
            ApiKeyHash = apiKeyHash,
            Name = "pipeline-test-machine",
            SerialNumber = $"sn-pipe-{Guid.NewGuid():N}",
            SystemId = $"sys-pipe-{Guid.NewGuid():N}",
            AssetTagNumber = null,
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = tokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId
        };
        long machineId = (long)await db.InsertWithIdentityAsync(machine);

        // Seed empty state rows so the streaming service has something to UPDATE
        await db.InsertAsync(new MachineStateSummary
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = "pipeline-test-machine",
            LastSeenAt = DateTimeOffset.UtcNow
        });
        await db.InsertAsync(new MachineStateDetail
        {
            MachineId = machineId
        });

        return (machineId, tenantId);
    }

    [Test]
    public async Task Pipeline_OldTelemetry_NotReturnedByStreamingServiceDateWindow()
    {
        // Arrange — seed a machine then submit one recent telemetry row via gRPC
        // and manually insert one old row with ReceivedAt 5 days ago.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-old-telem-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string recentEventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = recentEventId,
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 40 }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        // Insert an old telemetry row directly with ReceivedAt set to 5 days ago
        string oldEventId = Guid.NewGuid().ToString("N");
        MachineTelemetry oldRow = new()
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = TelemetryTypeIds.CpuUsage,
            Payload = "{\"cpu_usage_percent\":99}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-5),
            ServerReceivedAt = DateTimeOffset.UtcNow.AddDays(-5),
            SourceEventId = oldEventId
        };
        await db.InsertAsync(oldRow);

        // Act — query with the same 2-day window filter the streaming service uses
        DateTimeOffset streamingWindow = DateTimeOffset.UtcNow.AddDays(-2);
        List<MachineTelemetry> windowRows = await db.MachineTelemetry
            .Where(t => t.Id > 0 && t.ReceivedAt > streamingWindow)
            .Where(t => t.MachineId == machineId)
            .ToListAsync();

        // Assert — only the recent row should be returned; the old row is excluded by the date filter
        await Assert.That(windowRows.Count).IsEqualTo(1);
        await Assert.That(windowRows[0].SourceEventId).IsEqualTo(recentEventId);

        // Verify the old row does exist in the table (it was not filtered by anything else)
        List<MachineTelemetry> allRows = await db.MachineTelemetry
            .Where(t => t.MachineId == machineId)
            .ToListAsync();

        await Assert.That(allRows.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Pipeline_RecentTelemetry_ProcessedByStreamingService()
    {
        // Arrange — submit telemetry via gRPC and verify it falls within
        // the 2-day streaming window and is processed into state tables.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-recent-telem-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 82 }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        // Act — verify the row is within the streaming window
        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        DateTimeOffset streamingWindow = DateTimeOffset.UtcNow.AddDays(-2);
        List<MachineTelemetry> windowRows = await db.MachineTelemetry
            .Where(t => t.Id > 0 && t.ReceivedAt > streamingWindow)
            .Where(t => t.MachineId == machineId)
            .ToListAsync();

        await Assert.That(windowRows.Count).IsEqualTo(1);
        await Assert.That(windowRows[0].SourceEventId).IsEqualTo(eventId);

        // Project through the production collapse-and-apply path and verify state tables update.
        await ProjectRowsAsync(db, row!);

        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.CpuUsagePercent).IsEqualTo(82);
    }

    [Test]
    public async Task Pipeline_DefaultProtoZeroValue_RoundTripsToZeroNotNull()
    {
        // Protobuf int32 default is 0. When CpuUsagePercent=0, the persisted JSON must contain
        // "cpu_usage_percent":0 and the streaming service must store 0 in MachineStateSummary,
        // not null. A null means "no data" which is semantically different from "0% CPU usage."
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-zero-cpu-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 0 }
                }
            }
        };

        // Act - submit via gRPC and process through the streaming service
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(row).IsNotNull();

        await ProjectRowsAsync(db, row!);

        // Assert - CpuUsagePercent must be exactly 0, not null
        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.CpuUsagePercent).IsNotNull();
        await Assert.That(summary!.CpuUsagePercent).IsEqualTo(0);
    }

    [Test]
    public async Task Pipeline_EmptyEnvelope_ReturnsSuccessWithNoAcknowledgedIds()
    {
        // An envelope with zero items should succeed gracefully without crashing or rejecting.
        // The server must acknowledge the batch but report no event IDs and insert no rows.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-empty-envelope-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            // No items added intentionally
        };

        // Act
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        // Assert - the server should accept an empty envelope without error
        await Assert.That(ack.Success).IsTrue();
        await Assert.That(ack.AcknowledgedEventIds.Count).IsEqualTo(0);

        // Verify no telemetry rows were inserted for this machine
        int telemetryCount = await db.MachineTelemetry
            .Where(t => t.MachineId == machineId)
            .CountAsync();

        await Assert.That(telemetryCount).IsEqualTo(0);
    }

    [Test]
    public async Task Pipeline_MixedAges_OnlyRecentTelemetryInStreamingWindow()
    {
        // When the streaming service polls, it should only process telemetry within its
        // 2-day window. Old rows that predate the window must be excluded by the date filter,
        // even if they exist in the same table.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "pipeline-mixed-ages-key";
        (long machineId, int tenantId) = await SeedMachineWithStateRows(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string cpuEventId = Guid.NewGuid().ToString("N");
        string memEventId = Guid.NewGuid().ToString("N");

        // Submit 2 recent telemetry items via gRPC (CPU and Memory)
        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = cpuEventId,
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 45 }
                },
                new TelemetryItem
                {
                    EventId = memEventId,
                    Type = TelemetryTypes.MemoryUtilizationType,
                    MemoryUtilization = new MemoryUtilizationRecord
                    {
                        MemoryTotal = 16_000_000_000,
                        MemoryUsed = 8_000_000_000,
                        MemoryUsagePercent = 50
                    }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);
        await Assert.That(ack.Success).IsTrue();

        // Manually insert an old telemetry row with ReceivedAt 5 days ago (DiskUtilization type)
        string oldDiskEventId = Guid.NewGuid().ToString("N");
        MachineTelemetry oldRow = new()
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = TelemetryTypeIds.DiskUsage,
            Payload = "{\"disks\":[{\"device\":\"/dev/sda1\",\"path\":\"/\",\"usage_percent\":88}]}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-5),
            ServerReceivedAt = DateTimeOffset.UtcNow.AddDays(-5),
            SourceEventId = oldDiskEventId
        };
        await db.InsertAsync(oldRow);

        // Act - query with the 2-day streaming window filter
        DateTimeOffset streamingWindow = DateTimeOffset.UtcNow.AddDays(-2);
        List<MachineTelemetry> windowRows = await db.MachineTelemetry
            .Where(t => t.MachineId == machineId && t.ReceivedAt > streamingWindow)
            .ToListAsync();

        // Assert - only the 2 recent gRPC-submitted rows pass the date filter
        await Assert.That(windowRows.Count).IsEqualTo(2);

        // Verify the old disk row is not in the filtered results
        bool oldRowInWindow = windowRows.Exists(r => r.SourceEventId == oldDiskEventId);
        await Assert.That(oldRowInWindow).IsFalse();

        // Verify all 3 rows exist in an unfiltered query to prove it is the date filter
        // that excluded the old row, not some other mechanism
        List<MachineTelemetry> allRows = await db.MachineTelemetry
            .Where(t => t.MachineId == machineId)
            .ToListAsync();

        await Assert.That(allRows.Count).IsEqualTo(3);
    }
}
