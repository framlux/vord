// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.FleetManagement.Grpc.AgentTelemetry;
using Grpc.Core;
using Grpc.Net.Client;
using LinqToDB;
using LinqToDB.Async;
using System.Security.Cryptography;
using System.Text;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Grpc;

/// <summary>
/// Functional tests for telemetry submission via gRPC.
/// Tests the full pipeline: API key auth, subscription validation, deduplication, and persistence.
/// </summary>
public sealed class TelemetrySubmissionTests
{
    [Test]
    public async Task SubmitTelemetry_ValidApiKeyAndActiveSubscription_Succeeds()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "telemetry-valid-api-key";
        (long machineId, int tenantId) = await SeedMachineWithSubscription(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId = Guid.NewGuid().ToString("N");
        string batchId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = batchId,
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 42 }
                }
            }
        };

        // Act
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        // Assert — verify the acknowledgment contains expected fields
        await Assert.That(ack.Success).IsTrue();
        await Assert.That(ack.BatchId).IsEqualTo(batchId);
        await Assert.That(ack.AcknowledgedEventIds).Contains(eventId);
        await Assert.That(ack.AcknowledgedEventIds.Count).IsEqualTo(1);
        await Assert.That(ack.ErrorMessage).IsEmpty();

        // Verify telemetry was persisted
        MachineTelemetry? stored = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.MachineId).IsEqualTo(machineId);
    }

    [Test]
    public async Task SubmitTelemetry_CanceledSubscription_ReturnsSubscriptionNotActiveError()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "telemetry-inactive-sub-key";
        (long machineId, int tenantId) = await SeedMachineWithSubscription(
            db, apiKey, subscriptionStatus: SubscriptionStatus.Canceled);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string batchId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = batchId,
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 10 }
                }
            }
        };

        // Act
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        // Assert — canceled subscription should be rejected with a specific error message
        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.BatchId).IsEqualTo(batchId);
        await Assert.That(ack.ErrorMessage).IsEqualTo("Tenant subscription is not active");
    }

    [Test]
    public async Task SubmitTelemetry_PastDueSubscription_ReturnsSubscriptionNotActiveError()
    {
        // Arrange — PastDue subscriptions should also be rejected since only Active is accepted
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "telemetry-pastdue-sub-key";
        (long machineId, int tenantId) = await SeedMachineWithSubscription(
            db, apiKey, subscriptionStatus: SubscriptionStatus.PastDue);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string batchId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = batchId,
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 15 }
                }
            }
        };

        // Act
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        // Assert — PastDue is not Active, so telemetry should be rejected
        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.BatchId).IsEqualTo(batchId);
        await Assert.That(ack.ErrorMessage).IsEqualTo("Tenant subscription is not active");
    }

    [Test]
    public async Task SubmitTelemetry_MachineWithNoSubscription_ReturnsSubscriptionNotActiveError()
    {
        // Arrange — machine belongs to a tenant with no subscription record at all
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "telemetry-no-sub-key";
        (long machineId, int tenantId) = await SeedMachineWithoutSubscription(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string batchId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = batchId,
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 20 }
                }
            }
        };

        // Act
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        // Assert — null subscription should be treated as not active
        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.BatchId).IsEqualTo(batchId);
        await Assert.That(ack.ErrorMessage).IsEqualTo("Tenant subscription is not active");
    }

    [Test]
    public async Task SubmitTelemetry_WithoutApiKey_ReturnsUnauthenticated()
    {
        // Arrange
        using FunctionalTestFactory factory = new();

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 5 }
                }
            }
        };

        // Act & Assert
        RpcException? exception = null;
        try
        {
            await client.SubmitTelemetryAsync(envelope);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task SubmitTelemetry_MachineIdMismatch_ReturnsMismatchError()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "telemetry-mismatch-key";
        (long machineId, int tenantId) = await SeedMachineWithSubscription(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        // Send a different machine ID in the header than what the API key resolves to
        Metadata headers = new()
        {
            { "x-api-key", apiKey },
            { "x-machine-id", "99999" }
        };

        string batchId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = batchId,
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 5 }
                }
            }
        };

        // Act
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        // Assert — machine ID mismatch should be rejected with a specific error
        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.BatchId).IsEqualTo(batchId);
        await Assert.That(ack.ErrorMessage).IsEqualTo("Machine ID mismatch between API key and header");
    }

    [Test]
    public async Task SubmitTelemetry_MultipleTelemetryItems_AllPersistedAndAcknowledged()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "telemetry-multi-item-key";
        (long machineId, int tenantId) = await SeedMachineWithSubscription(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string eventId1 = Guid.NewGuid().ToString("N");
        string eventId2 = Guid.NewGuid().ToString("N");
        string eventId3 = Guid.NewGuid().ToString("N");
        string batchId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = batchId,
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId1,
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 25 }
                },
                new TelemetryItem
                {
                    EventId = eventId2,
                    Type = TelemetryTypes.MemoryUtilizationType,
                    MemoryUtilization = new MemoryUtilizationRecord
                    {
                        MemoryTotal = 8_000_000_000,
                        MemoryUsed = 4_000_000_000,
                        MemoryUsagePercent = 50
                    }
                },
                new TelemetryItem
                {
                    EventId = eventId3,
                    Type = TelemetryTypes.DiskUtilizationType,
                    DiskUtilization = new DiskUtilizationRecord
                    {
                        Disks =
                        {
                            new DiskUtilizationEntry
                            {
                                Path = "/",
                                Blocks = 500_000_000,
                                BlocksFree = 250_000_000,
                            }
                        }
                    }
                }
            }
        };

        // Act
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        // Assert — all items should be acknowledged and persisted
        await Assert.That(ack.Success).IsTrue();
        await Assert.That(ack.BatchId).IsEqualTo(batchId);
        await Assert.That(ack.AcknowledgedEventIds.Count).IsEqualTo(3);
        await Assert.That(ack.AcknowledgedEventIds).Contains(eventId1);
        await Assert.That(ack.AcknowledgedEventIds).Contains(eventId2);
        await Assert.That(ack.AcknowledgedEventIds).Contains(eventId3);

        int storedCount = await db.MachineTelemetry
            .Where(t => t.MachineId == machineId)
            .CountAsync();
        await Assert.That(storedCount).IsEqualTo(3);
    }

    [Test]
    public async Task SubmitTelemetry_ExceedsMaxItemsPerEnvelope_ReturnsMaxItemCountError()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "telemetry-overflow-key";
        (long machineId, int tenantId) = await SeedMachineWithSubscription(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string batchId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = batchId,
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        // Add 501 items (max is 500)
        for (int i = 0; i < 501; i++)
        {
            envelope.Items.Add(new TelemetryItem
            {
                EventId = Guid.NewGuid().ToString("N"),
                Type = TelemetryTypes.CpuUtilizationType,
                CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 1 }
            });
        }

        // Act
        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        // Assert — exceeding item limit should return a specific error
        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.BatchId).IsEqualTo(batchId);
        await Assert.That(ack.ErrorMessage).IsEqualTo("Envelope exceeds maximum item count of 500");
    }

    [Test]
    public async Task SubmitTelemetry_AgentClockSkewedForward_RejectsWithClockSkewError()
    {
        // Intent: A machine whose clock is 10 minutes ahead of server time sends
        // unreliable timestamps. The server must reject the envelope.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "telemetry-clock-ahead-key";
        (long machineId, int tenantId) = await SeedMachineWithSubscription(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string batchId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = batchId,
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.UtcNow.AddMinutes(10)),
            Items =
            {
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).Contains("clock skew");

        // Verify nothing was persisted
        int count = await db.MachineTelemetry
            .Where(t => t.MachineId == machineId)
            .CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitTelemetry_AgentClockSkewedBackward_RejectsWithClockSkewError()
    {
        // Intent: A machine whose clock is 10 minutes behind server time.
        // Same rejection as forward skew — both directions are unreliable.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "telemetry-clock-behind-key";
        (long machineId, int tenantId) = await SeedMachineWithSubscription(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        string batchId = Guid.NewGuid().ToString("N");

        TelemetryEnvelope envelope = new()
        {
            BatchId = batchId,
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.UtcNow.AddMinutes(-10)),
            Items =
            {
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).Contains("clock skew");
    }

    [Test]
    public async Task SubmitTelemetry_AgentClockWithinSkewLimit_Succeeds()
    {
        // Intent: A machine with minor clock drift (2 minutes) should be accepted.
        // This is within the ±5 minute tolerance.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "telemetry-clock-ok-key";
        (long machineId, int tenantId) = await SeedMachineWithSubscription(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.UtcNow.AddMinutes(2)),
            Items =
            {
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 30 }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        await Assert.That(ack.Success).IsTrue();
    }

    [Test]
    public async Task SubmitTelemetry_NoAgentTimestamp_RejectsWithMissingTimestampError()
    {
        // Intent: Every envelope must include agent_timestamp so the server can
        // validate clock accuracy. Missing timestamps are rejected.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "telemetry-no-timestamp-key";
        (long machineId, int tenantId) = await SeedMachineWithSubscription(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };

        TelemetryEnvelope envelope = new()
        {
            BatchId = Guid.NewGuid().ToString("N"),
            // No AgentTimestamp set
            Items =
            {
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 20 }
                }
            }
        };

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).IsEqualTo("agent_timestamp is required");

        // Verify nothing was persisted
        int count = await db.MachineTelemetry
            .Where(t => t.MachineId == machineId)
            .CountAsync();
        await Assert.That(count).IsEqualTo(0);
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

    private static async Task<(long machineId, int tenantId)> SeedMachineWithSubscription(
        DatabaseContext db,
        string plaintextApiKey,
        SubscriptionStatus subscriptionStatus = SubscriptionStatus.Active)
    {
        Tenant tenant = new()
        {
            Name = $"Telemetry Test Tenant {Guid.NewGuid():N}",
            ExternalId = $"ext-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        int tenantId = (int)(long)await db.InsertWithIdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Free,
            Status = subscriptionStatus,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(subscription);

        return await SeedMachineForTenant(db, tenantId, plaintextApiKey);
    }

    private static async Task<(long machineId, int tenantId)> SeedMachineWithoutSubscription(
        DatabaseContext db,
        string plaintextApiKey)
    {
        Tenant tenant = new()
        {
            Name = $"Telemetry Test Tenant NoSub {Guid.NewGuid():N}",
            ExternalId = $"ext-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        int tenantId = (int)(long)await db.InsertWithIdentityAsync(tenant);

        // Intentionally skip inserting a TenantSubscription

        return await SeedMachineForTenant(db, tenantId, plaintextApiKey);
    }

    private static async Task<(long machineId, int tenantId)> SeedMachineForTenant(
        DatabaseContext db,
        int tenantId,
        string plaintextApiKey)
    {
        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Telemetry Test Token",
            CreatedByUserId = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false
        };
        long tokenId = (long)await db.InsertWithIdentityAsync(token);

        string apiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextApiKey)));
        Machine machine = new()
        {
            ApiKeyHash = apiKeyHash,
            Name = "telemetry-test-machine",
            SerialNumber = $"sn-tel-{Guid.NewGuid():N}",
            SystemId = $"sys-tel-{Guid.NewGuid():N}",
            AssetTagNumber = null,
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = tokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId
        };
        long machineId = (long)await db.InsertWithIdentityAsync(machine);

        return (machineId, tenantId);
    }
}
