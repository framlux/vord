// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Extensions;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Proves the fleet gauge actually emits from a worker container, not merely that the class works
/// when a test constructs it.
/// </summary>
/// <remarks>
/// This is the only test in the suite that could catch the defect it exists for. Nothing injects
/// <see cref="FleetMetrics"/>, so a plain singleton registration is never resolved: the constructor
/// never runs, the observable instrument is never created, and <c>vord_fleet_machines</c> never
/// appears in Prometheus — while every unit test passes, because those tests construct the class
/// directly. The series matters more than most: the ingest-stalled rule is gated on it, and phase 5
/// nominates its absence as the dead-man switch for the whole work item.
/// </remarks>
public sealed class FleetMetricsRegistrationTests
{
    [Test]
    public async Task WorkerHost_StartingTheInitialiser_MakesTheFleetGaugeCollectable()
    {
        using ServiceProvider provider = BuildWorkerProvider();

        // Starting the hosted service is what resolves the observable descriptors, which is the only
        // thing that constructs a gauge class nothing else depends on.
        MetricSeriesInitialiser initialiser = provider.GetServices<IHostedService>()
            .OfType<MetricSeriesInitialiser>()
            .Single();

        IMeterFactory meterFactory = provider.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> collector = new(meterFactory, VordMeter.Name, "vord.fleet.machines");

        await initialiser.StartAsync(CancellationToken.None);

        collector.RecordObservableInstruments();

        await Assert.That(collector.GetMeasurementSnapshot().Count).IsEqualTo(4);
    }

    [Test]
    public async Task WorkerHost_StartingTheInitialiser_MakesThePausedGaugeResolveFromTheContainer()
    {
        // The paused callback swallows any failure to resolve what it needs and reports nothing, so a
        // new dependency would be invisible from the unit tests, which hand the class its
        // repositories directly. Resolving through a container that holds only the services listed
        // in BuildWorkerProvider pins that set: a dependency added without updating it fails here
        // instead of silently dropping the series.
        using ServiceProvider provider = BuildWorkerProvider();

        MetricSeriesInitialiser initialiser = provider.GetServices<IHostedService>()
            .OfType<MetricSeriesInitialiser>()
            .Single();

        IMeterFactory meterFactory = provider.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> collector = new(meterFactory, VordMeter.Name, "vord.fleet.paused_machines");

        await initialiser.StartAsync(CancellationToken.None);

        collector.RecordObservableInstruments();

        // Three registered machines under a Free limit of one.
        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(2L);
    }

    private static ServiceProvider BuildWorkerProvider()
    {
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetFleetMachineCountsByHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<short, int>>(new Dictionary<short, int>()));

        IMachineRepository machines = Substitute.For<IMachineRepository>();
        machines.GetRegisteredMachineCountsForAllTenantsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Dictionary<int, int> { [1] = 3 }));

        ISubscriptionRepository subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.GetSubscriptionsForTenantsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TenantSubscription>()));

        ITierFeatureLimitRepository tierRows = Substitute.For<ITierFeatureLimitRepository>();
        tierRows.GetAllLimitsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TierFeatureLimit>()));

        ITenantSubscriptionOverrideRepository overrides = Substitute.For<ITenantSubscriptionOverrideRepository>();
        overrides.GetOverridesForTenantsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TenantSubscriptionOverride>()));

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Observability:OtlpEndpoint"] = "http://collector:4317",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<StreamingOptions>();
        services.AddOptions<ObjectStorageOptions>();

        // The hosted deployment, because self-hosted short-circuits the paused gauge to zero before it
        // resolves anything else.
        services.AddOptions<DeploymentOptions>().Configure(options => options.SelfHosted = false);
        services.AddSingleton<DeploymentMode>();
        services.AddOptions<TierDefaultOptions>().Configure(options => options.Free.MachineLimit = 1);
        services.AddScoped(_ => repository);
        services.AddScoped(_ => machines);
        services.AddScoped(_ => subscriptions);
        services.AddScoped(_ => tierRows);
        services.AddScoped(_ => overrides);

        services.AddCoreObservability(configuration, ObservabilityHost.ServicesWorker);

        return services.BuildServiceProvider();
    }
}
