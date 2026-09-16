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
/// Proves that a gRPC call's status reaches the request duration metric through the real pipeline,
/// which is the only way to prove it: the status is written into the response by the gRPC framework
/// after the endpoint has run, so nothing short of a real call exercises the ordering.
/// </summary>
/// <remarks>
/// The failure case is the point of the whole exercise. A failed gRPC call answers HTTP 200 and
/// puts its failure in a trailer, so the assertion that the status code tag reads 200 while the
/// gRPC tag reads a failure is what demonstrates the gap this closes.
/// </remarks>
public sealed class GrpcStatusMetricTagTests
{
    /// <summary>
    /// The meter the ASP.NET Core hosting layer publishes the request duration on.
    /// </summary>
    private const string HostingMeterName = "Microsoft.AspNetCore.Hosting";

    /// <summary>
    /// The built-in instrument the gRPC status is added to.
    /// </summary>
    private const string RequestDurationInstrument = "http.server.request.duration";

    /// <summary>
    /// Backstop for the measurement wait. It is a failure guard, not the mechanism: the wait is
    /// signalled by the collector when a measurement is recorded, so a healthy run never approaches
    /// this and a broken one fails rather than hanging.
    /// </summary>
    private static readonly TimeSpan MeasurementWaitTimeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task FailedCall_IsTaggedWithItsGrpcStatusDespiteAnsweringHttp200()
    {
        using FunctionalTestFactory factory = new();

        // The collector has to exist before the request: ASP.NET Core only exposes the tag feature
        // while something is listening, so a collector created afterwards would see an untagged
        // measurement and the test would pass against a completely broken pipeline.
        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<double> durations = new(meterFactory, HostingMeterName, RequestDurationInstrument);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.RegisterSystemAsync(
                BuildRequest("no-such-token-at-all", "sn-status-tag-bad", "sys-status-tag-bad")));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);

        CollectedMeasurement<double> measurement = await SingleGrpcMeasurement(durations);

        // HTTP alone reports this failed call as a success, which is exactly why the gRPC tag has
        // to exist for an error-rate rule to see anything at all.
        await Assert.That(measurement.Tags["http.response.status_code"]).IsEqualTo(200);
        await Assert.That(measurement.Tags[GrpcStatusMetricTagMiddleware.TagName])
            .IsEqualTo((int)StatusCode.InvalidArgument);
    }

    [Test]
    public async Task SuccessfulCall_IsTaggedWithTheOkStatus()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long _) = await SeedTenantWithToken(db, "status-tag-ok-token");
        await SeedActiveSubscription(db, tenantId);

        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<double> durations = new(meterFactory, HostingMeterName, RequestDurationInstrument);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RegisterSystemResponse response = await client.RegisterSystemAsync(
            BuildRequest("status-tag-ok-token", "sn-status-tag-ok", "sys-status-tag-ok"));

        await Assert.That(response.MachineId).IsGreaterThan(0);

        // Success is tagged too, so the denominator of an error-rate rule is the same instrument
        // and the same tag set as its numerator.
        CollectedMeasurement<double> measurement = await SingleGrpcMeasurement(durations);
        await Assert.That(measurement.Tags[GrpcStatusMetricTagMiddleware.TagName]).IsEqualTo((int)StatusCode.OK);
    }

    [Test]
    public async Task NonGrpcRequest_CarriesNoGrpcStatusTag()
    {
        // A tag added to every request would change the shape of the existing web series for no
        // reason, and the liveness probe is the highest-volume request the server answers.
        using FunctionalTestFactory factory = new();

        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<double> durations = new(meterFactory, HostingMeterName, RequestDurationInstrument);

        using HttpClient httpClient = factory.CreateClient();
        using HttpResponseMessage response = await httpClient.GetAsync("/healthz");

        await Assert.That(response.IsSuccessStatusCode).IsTrue();

        IReadOnlyList<CollectedMeasurement<double>> measurements = durations.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsGreaterThan(0);

        foreach (CollectedMeasurement<double> measurement in measurements)
        {
            await Assert.That(measurement.Tags.ContainsKey(GrpcStatusMetricTagMiddleware.TagName)).IsFalse();
        }
    }

    /// <summary>
    /// Returns the one measurement carrying a gRPC status tag, failing if there is not exactly one.
    /// The host answers background requests of its own, so filtering is what keeps this reading the
    /// call under test rather than whichever measurement happened to arrive first.
    /// </summary>
    private static async Task<CollectedMeasurement<double>> SingleGrpcMeasurement(MetricCollector<double> durations)
    {
        // The hosting layer records the duration as the request pipeline unwinds, which can happen
        // after the client has already read the response and its trailer, so a snapshot taken the
        // instant the call returns is sometimes still empty. The collector signals when a measurement
        // lands, so this waits on that signal rather than sleeping and re-reading; the exact-count
        // assertion below is unchanged, so a second tagged measurement still fails the test.
        await durations.WaitForMeasurementsAsync(minCount: 1).WaitAsync(MeasurementWaitTimeout);

        List<CollectedMeasurement<double>> tagged = durations.GetMeasurementSnapshot()
            .Where(measurement => measurement.Tags.ContainsKey(GrpcStatusMetricTagMiddleware.TagName) == true)
            .ToList();

        await Assert.That(tagged.Count).IsEqualTo(1);

        return tagged[0];
    }

    private static RegisterSystemRequest BuildRequest(string token, string serial, string systemId)
    {
        return new RegisterSystemRequest
        {
            Hostname = "status-tag-host",
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
            Name = $"Status Tag Tenant {Guid.NewGuid():N}",
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
            Name = "Status Tag Token",
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
}
