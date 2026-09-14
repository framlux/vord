// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Grpc.AgentTelemetry;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Test.Infrastructure;
using Grpc.Core;
using Grpc.Net.Client;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Grpc;

/// <summary>
/// Proves the ingest instruments are recorded on the paths that actually run, not merely that the
/// metric class works in isolation. The auth cases matter most: they exercise rejections that never
/// reach the telemetry service at all, and would be invisible to any instrument recorded inside it.
/// </summary>
public sealed class TelemetryIngestMetricsTests
{
    [Test]
    public async Task InvalidApiKey_IncrementsAuthRejectionsAndNotEnvelopes()
    {
        // Arrange — resolving from the factory starts the host, so the startup zero-series pass has
        // already happened by the time the collectors below begin observing.
        using FunctionalTestFactory factory = new();
        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();

        using MetricCollector<long> rejections = new(meterFactory, VordMeter.Name, "vord.ingest.auth_rejections");
        using MetricCollector<long> envelopes = new(meterFactory, VordMeter.Name, "vord.ingest.envelopes");

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);
        Metadata headers = new() { { "x-api-key", "not-a-real-key" } };

        // Act
        RpcException? exception = null;
        try
        {
            await client.SubmitTelemetryAsync(BuildEnvelope(), headers: headers);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        // Assert — the request never reached the service, so only the auth instrument moved.
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);

        IReadOnlyList<CollectedMeasurement<long>> rejected = rejections.GetMeasurementSnapshot();
        await Assert.That(rejected.Count).IsEqualTo(1);
        await Assert.That(rejected[0].Tags["reason"]).IsEqualTo("unknown_key");
        await Assert.That(envelopes.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task NoApiKey_IsItsOwnReason()
    {
        using FunctionalTestFactory factory = new();
        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> rejections = new(meterFactory, VordMeter.Name, "vord.ingest.auth_rejections");

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);

        try
        {
            await client.SubmitTelemetryAsync(BuildEnvelope());
        }
        catch (RpcException)
        {
            // The refusal is the subject of this test; its status code is asserted elsewhere.
        }

        IReadOnlyList<CollectedMeasurement<long>> rejected = rejections.GetMeasurementSnapshot();
        await Assert.That(rejected.Count).IsEqualTo(1);
        await Assert.That(rejected[0].Tags["reason"]).IsEqualTo("missing_key");
    }

    [Test]
    public async Task AcceptedEnvelope_IncrementsTheAcceptedOutcome()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "ingest-metrics-accepted-key";
        await SeedMachineWithSubscription(db, apiKey);

        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> envelopes = new(meterFactory, VordMeter.Name, "vord.ingest.envelopes");

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);
        Metadata headers = new() { { "x-api-key", apiKey } };

        TelemetryAck ack = await client.SubmitTelemetryAsync(BuildEnvelope(), headers: headers);

        await Assert.That(ack.Success).IsTrue();

        IReadOnlyList<CollectedMeasurement<long>> measurements = envelopes.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("accepted");
    }

    [Test]
    public async Task MalformedEnvelope_IsRejectedAndNotCountedAsUnavailable()
    {
        // A missing agent timestamp is the agent's defect and a retry will not fix it. Sharing a
        // bucket with server-side backpressure would make a broken agent fleet read as an outage.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "ingest-metrics-rejected-key";
        await SeedMachineWithSubscription(db, apiKey);

        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> envelopes = new(meterFactory, VordMeter.Name, "vord.ingest.envelopes");

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);
        Metadata headers = new() { { "x-api-key", apiKey } };

        TelemetryEnvelope envelope = BuildEnvelope();
        envelope.AgentTimestamp = null;

        TelemetryAck ack = await client.SubmitTelemetryAsync(envelope, headers: headers);

        await Assert.That(ack.Success).IsFalse();

        IReadOnlyList<CollectedMeasurement<long>> measurements = envelopes.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("rejected");
    }

    [Test]
    public async Task InactiveSubscription_IsItsOwnOutcomeNotARejection()
    {
        // A lapsed subscription is a billing state, not a broken agent. It gets its own outcome so
        // an operator can tell "customers stopped paying" from "customers stopped working".
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "ingest-metrics-not-entitled-key";
        await SeedMachineWithSubscription(db, apiKey, SubscriptionStatus.Canceled);

        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> envelopes = new(meterFactory, VordMeter.Name, "vord.ingest.envelopes");

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);
        Metadata headers = new() { { "x-api-key", apiKey } };

        TelemetryAck ack = await client.SubmitTelemetryAsync(BuildEnvelope(), headers: headers);

        await Assert.That(ack.Success).IsFalse();

        IReadOnlyList<CollectedMeasurement<long>> measurements = envelopes.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("not_entitled");
    }

    [Test]
    public async Task RefusedStream_IsCountedWhereNoEnvelopeExists()
    {
        // A stream refused at open carries no envelope, so the envelope counter cannot see it — and
        // a fleet refused at open looks exactly like a fleet that stopped sending.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "ingest-metrics-stream-refused-key";
        await SeedMachineWithSubscription(db, apiKey, SubscriptionStatus.Canceled);

        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> streams = new(meterFactory, VordMeter.Name, "vord.ingest.stream_rejections");
        using MetricCollector<long> envelopes = new(meterFactory, VordMeter.Name, "vord.ingest.envelopes");

        using GrpcChannel channel = CreateChannel(factory);
        Telemetry.TelemetryClient client = new(channel);
        Metadata headers = new() { { "x-api-key", apiKey } };

        using AsyncDuplexStreamingCall<TelemetryEnvelope, TelemetryAck> call =
            client.StreamTelemetry(headers: headers);
        await call.RequestStream.CompleteAsync();

        try
        {
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                // The server refuses the stream before any ack, so this body never runs.
            }
        }
        catch (RpcException)
        {
            // The refusal surfaces as a non-OK status, which is the behaviour under test.
        }

        IReadOnlyList<CollectedMeasurement<long>> rejections = streams.GetMeasurementSnapshot();
        await Assert.That(rejections.Count).IsEqualTo(1);
        await Assert.That(rejections[0].Tags["reason"]).IsEqualTo("not_entitled");
        await Assert.That(envelopes.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    private static TelemetryEnvelope BuildEnvelope()
    {
        return new TelemetryEnvelope
        {
            BatchId = Guid.NewGuid().ToString("N"),
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Items =
            {
                new TelemetryItem
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = TelemetryTypes.CpuUtilizationType,
                    CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 42 }
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

    /// <summary>
    /// Seeds a machine whose API key authenticates, mirroring the helper in
    /// <c>TelemetrySubmissionTests</c> in this folder. Keep the two in step.
    /// </summary>
    private static async Task<(long MachineId, int TenantId)> SeedMachineWithSubscription(
        DatabaseContext db,
        string plaintextApiKey,
        SubscriptionStatus subscriptionStatus = SubscriptionStatus.Active)
    {
        Tenant tenant = new()
        {
            Name = $"Ingest Metrics Tenant {Guid.NewGuid():N}",
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
            Status = subscriptionStatus,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(subscription);

        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Ingest Metrics Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false
        };
        long tokenId = (long)await db.InsertWithIdentityAsync(token);

        string apiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextApiKey)));
        Machine machine = new()
        {
            ApiKeyHash = apiKeyHash,
            Name = "ingest-metrics-machine",
            SerialNumber = $"sn-im-{Guid.NewGuid():N}",
            SystemId = $"sys-im-{Guid.NewGuid():N}",
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
