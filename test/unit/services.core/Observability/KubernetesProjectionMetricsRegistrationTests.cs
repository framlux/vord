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
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Proves the Kubernetes projector lag gauge emits from the process that runs the projector and from
/// no other, not merely that the class works when a test constructs it.
/// </summary>
/// <remarks>
/// Nothing injects <see cref="KubernetesProjectionMetrics"/>, so a plain singleton registration would
/// never be resolved: its constructor would never run and the gauge would never exist while every unit
/// test passed. The projector is registered only in the services worker, and a gauge in the API server
/// would report the same cursor from a process that does no projecting.
/// </remarks>
public sealed class KubernetesProjectionMetricsRegistrationTests
{
    private const string Instrument = "vord.kubernetes.projection.lag_seconds";

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task WorkerHost_StartingTheInitialiser_MakesTheLagGaugeCollectable()
    {
        using ServiceProvider provider = BuildProvider(ObservabilityHost.ServicesWorker);
        MetricSeriesInitialiser initialiser = provider.GetServices<IHostedService>()
            .OfType<MetricSeriesInitialiser>()
            .Single();
        IMeterFactory meterFactory = provider.GetRequiredService<IMeterFactory>();
        using MetricCollector<double> collector = new(meterFactory, VordMeter.Name, Instrument);

        await initialiser.StartAsync(CancellationToken.None);
        collector.RecordObservableInstruments();

        // The substituted repository holds an unprojected row received 90 seconds before the fake
        // clock, so the value also proves the gauge resolved its repository through the container.
        IReadOnlyList<CollectedMeasurement<double>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(90d);
    }

    [Test]
    public async Task ApiServerHost_StartingTheInitialiser_RegistersNoLagGauge()
    {
        using ServiceProvider provider = BuildProvider(ObservabilityHost.ApiServer);
        MetricSeriesInitialiser initialiser = provider.GetServices<IHostedService>()
            .OfType<MetricSeriesInitialiser>()
            .Single();
        IMeterFactory meterFactory = provider.GetRequiredService<IMeterFactory>();
        using MetricCollector<double> collector = new(meterFactory, VordMeter.Name, Instrument);

        await initialiser.StartAsync(CancellationToken.None);
        collector.RecordObservableInstruments();

        await Assert.That(provider.GetService<KubernetesProjectionMetrics>()).IsNull();
        await Assert.That(provider.GetServices<ObservableMetricsDescriptor>()
            .Any(descriptor => descriptor.MetricsType == typeof(KubernetesProjectionMetrics))).IsFalse();
        await Assert.That(collector.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task WorkerHost_RegistersTheGaugeClassOnceWithADescriptor()
    {
        using ServiceProvider provider = BuildProvider(ObservabilityHost.ServicesWorker);

        await Assert.That(provider.GetRequiredService<KubernetesProjectionMetrics>()).IsNotNull();
        await Assert.That(provider.GetServices<ObservableMetricsDescriptor>()
            .Count(descriptor => descriptor.MetricsType == typeof(KubernetesProjectionMetrics))).IsEqualTo(1);
    }

    private static ServiceProvider BuildProvider(ObservabilityHost host)
    {
        IKubernetesInventoryRepository repository = Substitute.For<IKubernetesInventoryRepository>();
        repository.GetProjectionCursorAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(7));
        repository.GetOldestUnprojectedKubernetesReceiptAsync(
                Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DateTimeOffset?>(Now.AddSeconds(-90)));

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Observability:OtlpEndpoint"] = "http://collector:4317",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddOptions<StreamingOptions>();
        services.AddOptions<ObjectStorageOptions>();
        services.AddOptions<DeploymentOptions>();
        services.AddSingleton<DeploymentMode>();
        services.AddScoped(_ => repository);

        services.AddCoreObservability(configuration, host);

        return services.BuildServiceProvider();
    }
}
