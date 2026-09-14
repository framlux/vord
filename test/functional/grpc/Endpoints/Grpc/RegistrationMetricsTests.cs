// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Grpc.AgentRegistration;
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
/// Proves the registration instruments are recorded on the paths a real agent takes, not merely
/// that the metric class works in isolation.
/// </summary>
/// <remarks>
/// The bad-token case matters most: it is the failure with no tenant in scope at all, so it is the
/// one that proves the unknown bucket is reachable from a real request rather than only from a unit
/// test. The machine-limit case is the commercially interesting one — a paying customer at their
/// tier cap who cannot onboard, and who has no way to tell us.
/// </remarks>
public sealed class RegistrationMetricsTests
{
    [Test]
    public async Task SuccessfulRegistration_RecordsRegisteredAndAttributesNothing()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long _) = await SeedTenantWithToken(db, "metrics-ok-token");
        await SeedActiveSubscription(db, tenantId);

        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> attempts = new(meterFactory, VordMeter.Name, "vord.registration.attempts");
        using MetricCollector<long> failures = new(meterFactory, VordMeter.Name, "vord.registration.failures");

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RegisterSystemResponse response = await client.RegisterSystemAsync(
            BuildRequest("metrics-ok-token", "sn-metrics-ok", "sys-metrics-ok"));

        await Assert.That(response.MachineId).IsGreaterThan(0);

        IReadOnlyList<CollectedMeasurement<long>> measurements = attempts.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("registered");
        await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task UnknownToken_RecordsInvalidTokenAgainstTheUnknownTenantBucket()
    {
        // The token is how a tenant is identified, so a token that resolves to nothing has no
        // tenant to attribute the failure to. That is the headline onboarding failure.
        using FunctionalTestFactory factory = new();

        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> attempts = new(meterFactory, VordMeter.Name, "vord.registration.attempts");
        using MetricCollector<long> failures = new(meterFactory, VordMeter.Name, "vord.registration.failures");

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        // The gRPC layer flattens every service-layer failure into one InvalidArgument, which is
        // precisely why the instrument lives in the service and not here.
        RpcException exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.RegisterSystemAsync(
                BuildRequest("no-such-token-at-all", "sn-metrics-bad", "sys-metrics-bad")));

        await Assert.That(exception.StatusCode).IsEqualTo(StatusCode.InvalidArgument);

        IReadOnlyList<CollectedMeasurement<long>> measurements = attempts.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("invalid_token");

        IReadOnlyList<CollectedMeasurement<long>> attributed = failures.GetMeasurementSnapshot();
        await Assert.That(attributed.Count).IsEqualTo(1);
        await Assert.That(attributed[0].Tags["tenant"]).IsEqualTo("unknown");
    }

    [Test]
    public async Task TenantAtItsMachineLimit_RecordsMachineLimitExceededAttributedToThatTenant()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long tokenId) = await SeedTenantWithToken(db, "metrics-limit-token");
        await SeedActiveSubscription(db, tenantId);

        // The Free tier allows three machines, so a fourth registration is refused at the cap.
        for (int index = 0; index < 3; index++)
        {
            await SeedMachine(db, tenantId, tokenId, index);
        }

        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> attempts = new(meterFactory, VordMeter.Name, "vord.registration.attempts");
        using MetricCollector<long> failures = new(meterFactory, VordMeter.Name, "vord.registration.failures");

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RpcException exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.RegisterSystemAsync(
                BuildRequest("metrics-limit-token", "sn-metrics-limit", "sys-metrics-limit")));

        await Assert.That(exception.StatusCode).IsEqualTo(StatusCode.InvalidArgument);

        IReadOnlyList<CollectedMeasurement<long>> measurements = attempts.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("machine_limit_exceeded");

        IReadOnlyList<CollectedMeasurement<long>> attributed = failures.GetMeasurementSnapshot();
        await Assert.That(attributed.Count).IsEqualTo(1);
        await Assert.That(attributed[0].Tags["tenant"]).IsEqualTo(tenantId.ToString());
        await Assert.That(attributed[0].Tags["outcome"]).IsEqualTo("machine_limit_exceeded");
    }

    private static RegisterSystemRequest BuildRequest(string token, string serial, string systemId)
    {
        return new RegisterSystemRequest
        {
            Hostname = "metrics-host",
            SerialNumber = serial,
            SystemId = systemId,
            RegistrationToken = token,
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
        };
    }

    private static GrpcChannel CreateChannel(FunctionalTestFactory factory)
    {
        HttpMessageHandler handler = new ResponseVersionHandler
        {
            InnerHandler = factory.Server.CreateHandler(),
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
    }

    private static async Task<(int TenantId, long TokenId)> SeedTenantWithToken(DatabaseContext db, string tokenPlaintext)
    {
        Tenant tenant = new()
        {
            Name = $"Test Tenant {Guid.NewGuid():N}",
            ExternalId = $"ext-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        };

        int tenantId = (int)(long)await db.InsertWithIdentityAsync(tenant);

        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tokenPlaintext))),
            Name = "Test Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false,
        };

        long tokenId = (long)await db.InsertWithIdentityAsync(token);

        return (tenantId, tokenId);
    }

    private static async Task SeedActiveSubscription(DatabaseContext db, int tenantId)
    {
        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await db.InsertAsync(subscription);
    }

    private static async Task SeedMachine(DatabaseContext db, int tenantId, long tokenId, int index)
    {
        Machine machine = new()
        {
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            Name = $"existing-{index}",
            SerialNumber = $"sn-existing-{index}-{Guid.NewGuid():N}",
            SystemId = $"sys-existing-{index}-{Guid.NewGuid():N}",
            AssetTagNumber = null,
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = tokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        };

        await db.InsertAsync(machine);
    }
}
