// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Grpc.AgentTelemetry;
using Framlux.FleetManagement.Test.Infrastructure;
using Grpc.Core;
using Grpc.Net.Client;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Grpc;

/// <summary>
/// Telemetry ingest in a self-hosted deployment. Unlocking entitlements has to reach the retention
/// stamp on every persisted row — a user interface that shows unlimited retention while the rows
/// are classified for one-day deletion is the failure this covers — and it must stop at tenant
/// deactivation, which is not an entitlement question.
/// </summary>
public sealed class SelfHostedTelemetryTests
{
    [Test]
    public async Task IngestedTelemetry_InSelfHosted_IsStampedLongRetention()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = $"self-hosted-retention-{Guid.NewGuid():N}";
        (long machineId, int tenantId) = await SeedFreeTenantWithMachine(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        string eventId = Guid.NewGuid().ToString("N");
        TelemetryAck ack = await client.SubmitTelemetryAsync(
            BuildEnvelope(eventId),
            headers: new Metadata { { "x-api-key", apiKey } });

        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);

        await Assert.That(row).IsNotNull();
        await Assert.That(row!.MachineId).IsEqualTo(machineId);
        await Assert.That(row.TenantId).IsEqualTo(tenantId);
        await Assert.That(row.RetentionClass).IsEqualTo(RetentionClass.Long);
    }

    /// <summary>
    /// Subscription status has no meaning in a self-hosted deployment: there is no payment provider
    /// and every billing endpoint is absent, so a row left Canceled by a database import or by
    /// switching an existing install to self-hosted cannot be repaired. Gating ingest on it would
    /// silence the fleet permanently.
    /// </summary>
    [Test]
    public async Task Ingest_ForCanceledSubscriptionOnActiveTenant_IsAccepted()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = $"self-hosted-canceled-{Guid.NewGuid():N}";
        (long machineId, int tenantId) = await SeedFreeTenantWithMachine(db, apiKey);

        await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Status, SubscriptionStatus.Canceled)
            .UpdateAsync();

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        string eventId = Guid.NewGuid().ToString("N");
        TelemetryAck ack = await client.SubmitTelemetryAsync(
            BuildEnvelope(eventId),
            headers: new Metadata { { "x-api-key", apiKey } });

        await Assert.That(ack.Success).IsTrue();

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);

        await Assert.That(row).IsNotNull();
        await Assert.That(row!.MachineId).IsEqualTo(machineId);
    }

    /// <summary>
    /// A self-hosted deployment answers entitlement questions permissively, but ingest eligibility
    /// is not one: the tenant's active flag still decides, so deactivation and pending deletion keep
    /// stopping telemetry within a single request. This test is the pair of the one above — together
    /// they pin that status was dropped and the active flag was not.
    /// </summary>
    [Test]
    public async Task Ingest_ForDeactivatedTenant_IsStillBlocked()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = $"self-hosted-deactivated-{Guid.NewGuid():N}";
        (_, int tenantId) = await SeedFreeTenantWithMachine(db, apiKey);

        await db.Tenants
            .Where(t => t.Id == tenantId)
            .Set(t => t.IsActive, false)
            .UpdateAsync();

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        string eventId = Guid.NewGuid().ToString("N");
        TelemetryAck ack = await client.SubmitTelemetryAsync(
            BuildEnvelope(eventId),
            headers: new Metadata { { "x-api-key", apiKey } });

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).IsEqualTo("Tenant subscription is not active");

        MachineTelemetry? row = await db.MachineTelemetry
            .FirstOrDefaultAsync(t => t.SourceEventId == eventId);

        await Assert.That(row).IsNull();
    }

    private static TelemetryEnvelope BuildEnvelope(string eventId)
    {
        return new TelemetryEnvelope
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = eventId,
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 55 }
                }
            }
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

    private static async Task<(long MachineId, int TenantId)> SeedFreeTenantWithMachine(
        DatabaseContext db,
        string plaintextApiKey)
    {
        Tenant tenant = new()
        {
            Name = $"Self Hosted Telemetry Tenant {Guid.NewGuid():N}",
            ExternalId = $"ext-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        int tenantId = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(subscription);

        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Self Hosted Telemetry Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false
        };
        long tokenId = await db.InsertWithInt64IdentityAsync(token);

        Machine machine = new()
        {
            ApiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextApiKey))),
            Name = $"self-hosted-telemetry-machine-{Guid.NewGuid():N}",
            SerialNumber = $"sn-sh-{Guid.NewGuid():N}",
            SystemId = $"sys-sh-{Guid.NewGuid():N}",
            AssetTagNumber = null,
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = tokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId
        };
        long machineId = await db.InsertWithInt64IdentityAsync(machine);

        return (machineId, tenantId);
    }
}
