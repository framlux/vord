// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Extensions;
using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the rule that instruments always register and only the exporter is conditional. A fresh
/// clone with no configuration must start, record, and export nothing — so a missing endpoint can
/// never be allowed to make the metric classes unresolvable, which would turn every call site into
/// a null check or a crash.
/// </summary>
public sealed class AddCoreObservabilityTests
{
    private static ServiceProvider Build(
        string? endpoint,
        ObservabilityHost host = ObservabilityHost.ApiServer,
        string? standardEnvironmentEndpoint = null)
    {
        Dictionary<string, string?> settings = new();
        if (endpoint is not null)
        {
            settings["Observability:OtlpEndpoint"] = endpoint;
        }

        if (standardEnvironmentEndpoint is not null)
        {
            settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = standardEnvironmentEndpoint;
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        ServiceCollection services = new();
        services.AddLogging();

        // The gauge classes read the clock, the deployment mode, the object-storage configuration
        // and the streaming shard count. Both hosts register all of these through AddCoreOptions,
        // AddCoreServices and AddBackgroundWorkers before observability is added, so supplying them
        // here keeps this helper an honest stand-in for a real process rather than a weaker one.
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<StreamingOptions>();
        services.AddOptions<ObjectStorageOptions>();
        services.AddOptions<DeploymentOptions>();
        services.AddSingleton<DeploymentMode>();

        services.AddCoreObservability(configuration, host);

        return services.BuildServiceProvider();
    }

    [Test]
    public async Task NoEndpointConfigured_MetricClassesStillResolve()
    {
        using ServiceProvider provider = Build(endpoint: null);

        IngestMetrics metrics = provider.GetRequiredService<IngestMetrics>();

        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task NoEndpointConfigured_RecordingDoesNotThrow()
    {
        using ServiceProvider provider = Build(endpoint: null);
        IngestMetrics metrics = provider.GetRequiredService<IngestMetrics>();

        await Assert.That(() => metrics.RecordEnvelope(IngestOutcome.Accepted)).ThrowsNothing();
    }

    [Test]
    public async Task EndpointConfigured_MetricClassesResolve()
    {
        using ServiceProvider provider = Build("http://collector:4317");

        IngestMetrics metrics = provider.GetRequiredService<IngestMetrics>();

        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task ServiceNameComesFromTheHostNotConfiguration()
    {
        using ServiceProvider provider = Build("http://collector:4317", ObservabilityHost.ServicesWorker);

        IOptions<ObservabilityOptions> options = provider.GetRequiredService<IOptions<ObservabilityOptions>>();

        await Assert.That(options.Value.ServiceName).IsEqualTo("services-worker");
    }

    [Test]
    public async Task StandardOtlpEnvironmentVariable_EnablesExportOnItsOwn()
    {
        // The spec promises a self-hoster can point the standard variable at their own collector
        // with no code change. If only the vord-specific key opened the gate, that promise would
        // fail silently — the exporter would never be registered to read the variable at all.
        using ServiceProvider provider = Build(
            endpoint: null,
            standardEnvironmentEndpoint: "http://self-hosted-collector:4317");

        IOptions<ObservabilityOptions> options = provider.GetRequiredService<IOptions<ObservabilityOptions>>();

        await Assert.That(options.Value.IsExportEnabled).IsTrue();
        await Assert.That(options.Value.OtlpEndpoint).IsEqualTo("http://self-hosted-collector:4317");
    }

    [Test]
    public async Task ExplicitSettingWinsOverTheStandardEnvironmentVariable()
    {
        using ServiceProvider provider = Build(
            endpoint: "http://otel-agent:4317",
            standardEnvironmentEndpoint: "http://self-hosted-collector:4317");

        IOptions<ObservabilityOptions> options = provider.GetRequiredService<IOptions<ObservabilityOptions>>();

        await Assert.That(options.Value.OtlpEndpoint).IsEqualTo("http://otel-agent:4317");
    }

    [Test]
    public async Task NoEndpointAnywhere_ExportStaysOff()
    {
        using ServiceProvider provider = Build(endpoint: null);

        IOptions<ObservabilityOptions> options = provider.GetRequiredService<IOptions<ObservabilityOptions>>();

        await Assert.That(options.Value.IsExportEnabled).IsFalse();
    }

    [Test]
    public async Task IngestMetricsIsRegisteredOnTheApiServerOnly()
    {
        // Ingest is recorded in the API server's telemetry service and its authentication handler.
        // Registering every class in every process is what would eventually put a gauge in a process
        // whose backing service is absent, where the observable callback throws into the SDK.
        using ServiceProvider apiServer = Build("http://collector:4317", ObservabilityHost.ApiServer);
        using ServiceProvider worker = Build("http://collector:4317", ObservabilityHost.ServicesWorker);

        await Assert.That(apiServer.GetService<IngestMetrics>()).IsNotNull();
        await Assert.That(worker.GetService<IngestMetrics>()).IsNull();
    }

    [Test]
    public async Task MetricClassesAreSingletons()
    {
        using ServiceProvider provider = Build("http://collector:4317");

        IngestMetrics first = provider.GetRequiredService<IngestMetrics>();
        IngestMetrics second = provider.GetRequiredService<IngestMetrics>();

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task TheSameInstanceIsRegisteredForSeriesInitialisation()
    {
        // The initialiser must pre-record on the instruments the call sites use. Registering the
        // class twice would give the hosted service its own instance whose zeros land on a second
        // set of instruments nobody records to.
        using ServiceProvider provider = Build("http://collector:4317");

        IngestMetrics metrics = provider.GetRequiredService<IngestMetrics>();
        IInitialisableMetrics[] initialisable = provider.GetServices<IInitialisableMetrics>().ToArray();

        // The ingest class appears exactly once, and it is the very instance the call sites resolve.
        // A second registration would give the hosted service its own copy, whose zeros would land
        // on instruments nobody records to.
        await Assert.That(initialisable.OfType<IngestMetrics>().Count()).IsEqualTo(1);
        await Assert.That(ReferenceEquals(initialisable.OfType<IngestMetrics>().Single(), metrics)).IsTrue();

        // Every registered initialisable is a distinct class; none is registered twice.
        await Assert.That(initialisable.Select(m => m.GetType()).Distinct().Count())
            .IsEqualTo(initialisable.Length);
    }

    [Test]
    public async Task SeriesInitialisationRunsAsAHostedService()
    {
        // InitialiseSeries operates on constructed instruments, so it cannot run during
        // registration: at that point no instance exists. A hosted service is the first moment
        // there is something to call it on.
        using ServiceProvider provider = Build("http://collector:4317");

        IHostedService[] hosted = provider.GetServices<IHostedService>().ToArray();

        await Assert.That(hosted.Any(service => service is MetricSeriesInitialiser)).IsTrue();
    }

    [Test]
    public async Task SeriesInitialisation_OnAHostThatDoesNotOwnEveryMetricClass_StartsCleanly()
    {
        // The worker owns the shared classes but not the API server's ingest instruments, so the
        // initialiser resolves a partial set. Starting must be clean rather than a resolution
        // failure — a host must never be unable to start because a class belongs to its sibling.
        using ServiceProvider provider = Build("http://collector:4317", ObservabilityHost.ServicesWorker);
        MetricSeriesInitialiser initialiser = provider.GetServices<IHostedService>()
            .OfType<MetricSeriesInitialiser>()
            .Single();

        await initialiser.StartAsync(CancellationToken.None);
        await initialiser.StopAsync(CancellationToken.None);

        IInitialisableMetrics[] initialisable = provider.GetServices<IInitialisableMetrics>().ToArray();

        await Assert.That(initialisable.OfType<IngestMetrics>().Any()).IsFalse();
        await Assert.That(initialisable.OfType<EmailMetrics>().Any()).IsTrue();
    }
}
