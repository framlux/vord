// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

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
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetFleetMachineCountsByHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<short, int>>(new Dictionary<short, int>()));

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
        services.AddOptions<DeploymentOptions>();
        services.AddSingleton<DeploymentMode>();
        services.AddScoped(_ => repository);

        services.AddCoreObservability(configuration, ObservabilityHost.ServicesWorker);

        using ServiceProvider provider = services.BuildServiceProvider();

        // Starting the hosted service is what resolves the observable markers, which is the only
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
}
