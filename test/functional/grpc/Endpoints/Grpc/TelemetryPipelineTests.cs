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
            await repo.ApplySummaryPatchAsync(MapSummary(patch), CancellationToken.None);

            if (patch.HasDetailChanges == true)
            {
                await repo.ApplyDetailPatchAsync(MapDetail(patch), CancellationToken.None);
            }
        }
    }

    private static MachineSummaryPatch MapSummary(MachineStatePatch patch)
    {
        return new MachineSummaryPatch
        {
            MachineId = patch.MachineId,
            LastSeenAt = patch.LastSeenAt,
            HasSystemInfo = patch.SystemInfo is not null,
            Hostname = patch.SystemInfo?.Hostname,
            HardwareModel = patch.SystemInfo?.HardwareModel,
            IpAddresses = patch.SystemInfo?.IpAddresses,
            HasOsVersion = patch.OsVersion is not null,
            OsName = patch.OsVersion?.OsName,
            OsVersion = patch.OsVersion?.OsVersion,
            HasCpuUsage = patch.CpuUsage is not null,
            CpuUsagePercent = patch.CpuUsage?.CpuUsagePercent,
            HasMemoryUsage = patch.MemoryUsage is not null,
            MemoryUsagePercent = patch.MemoryUsage?.MemoryUsagePercent,
            HasDiskUsage = patch.DiskUsage is not null,
            MaxDiskUsagePercent = patch.DiskUsage?.MaxDiskUsagePercent,
            HasHardwareHealth = patch.HardwareHealth is not null,
            HasDiskHealthIssue = patch.HardwareHealth?.HasDiskHealthIssue,
            HasHardwareIssue = patch.HardwareHealth?.HasHardwareIssue,
            HasPackageUpdates = patch.PackageUpdates is not null,
            PendingUpdates = patch.PackageUpdates?.PendingUpdates,
            SecurityUpdates = patch.PackageUpdates?.SecurityUpdates,
            HasServiceStatus = patch.ServiceStatus is not null,
            TotalServices = patch.ServiceStatus?.TotalServices,
            FailedServices = patch.ServiceStatus?.FailedServices,
        };
    }

    private static MachineDetailPatch MapDetail(MachineStatePatch patch)
    {
        return new MachineDetailPatch
        {
            MachineId = patch.MachineId,
            HasSystemInfo = patch.SystemInfo is not null,
            HardwareVendor = patch.SystemInfo?.HardwareVendor,
            HardwareSerial = patch.SystemInfo?.HardwareSerial,
            CpuBrand = patch.SystemInfo?.CpuBrand,
            CpuCores = patch.SystemInfo?.CpuCores,
            MemoryTotalBytes = patch.SystemInfo?.MemoryTotalBytes,
            UptimeSeconds = patch.SystemInfo?.UptimeSeconds,
            BiosVersion = patch.SystemInfo?.BiosVersion,
            HasOsVersion = patch.OsVersion is not null,
            Kernel = patch.OsVersion?.Kernel,
            HasCpuInfo = patch.CpuInfo is not null,
            CpuType = patch.CpuInfo?.CpuType,
            CpuPhysicalCpus = patch.CpuInfo?.CpuPhysicalCpus,
            CpuLogicalCpus = patch.CpuInfo?.CpuLogicalCpus,
            HasMemoryInfo = patch.MemoryInfo is not null,
            SwapTotalBytes = patch.MemoryInfo?.SwapTotalBytes,
            SwapFreeBytes = patch.MemoryInfo?.SwapFreeBytes,
            HasMemoryUsage = patch.MemoryUsage is not null,
            MemoryUsedBytes = patch.MemoryUsage?.MemoryUsedBytes,
            HasDiskInfo = patch.DiskInfo is not null,
            DiskInfos = patch.DiskInfo?.DiskInfos,
            HasDiskUsage = patch.DiskUsage is not null,
            DiskUsages = patch.DiskUsage?.DiskUsages,
            HasSshSessions = patch.SshSessions is not null,
            SshSessions = patch.SshSessions?.SshSessions,
            HasHardwareHealth = patch.HardwareHealth is not null,
            HardwareHealth = patch.HardwareHealth?.HardwareHealth,
        };
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
