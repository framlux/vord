// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using System.Reflection;

namespace Framlux.FleetManagement.Services.Core.Extensions;

/// <summary>
/// Registers OpenTelemetry metrics for api-server and services-worker.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the metric classes this host owns and, when an OTLP endpoint is configured, the
    /// exporter that sends their measurements to the collector.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration, read for the <c>Observability</c> section.</param>
    /// <param name="host">Which process this is, deciding both <c>service.name</c> and which instruments it owns.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <remarks>
    /// The metric classes register unconditionally. Only the exporter is conditional, so a
    /// deployment with no collector records into instruments nobody is listening to rather than
    /// forcing every call site to ask whether observability is switched on.
    /// </remarks>
    public static IServiceCollection AddCoreObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        ObservabilityHost host)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string serviceName = ResolveServiceName(host);
        string? endpoint = ResolveEndpoint(configuration);

        services.AddOptions<ObservabilityOptions>()
            .Bind(configuration.GetSection("Observability"))
            .Configure(options =>
            {
                options.ServiceName = serviceName;
                options.OtlpEndpoint = endpoint;
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ObservabilityOptions>, ObservabilityOptionsValidator>();

        services.AddMetrics();

        // The metric classes take this as a constructor argument, so it has to be resolvable rather
        // than closed over by a factory lambda. Nothing else registers the enum. The non-generic
        // overload is required because the generic one only accepts reference types.
        services.AddSingleton(typeof(ObservabilityHost), host);

        if (host == ObservabilityHost.ApiServer)
        {
            AddInitialisable<IngestMetrics>(services);
        }

        if (host == ObservabilityHost.ServicesWorker)
        {
            // The descriptor is what guarantees construction. This class is also injected into the
            // streaming service, so today it would be built anyway — but that is a coincidence of
            // the current wiring, and relying on it is how a gauge comes to emit nothing at all.
            AddObservable<ProjectionMetrics>(services);

            // Nothing injects the fleet gauge — it has no call sites at all — so without its
            // descriptor it would never be constructed, its instrument would never be created, and
            // vord_fleet_machines would never exist while every test still passed.
            AddObservable<FleetMetrics>(services);
        }

        // Registered in both processes because AddCoreServices registers AlertDeliveryService
        // unconditionally on both. A host-conditional registration here would leave one of them
        // unable to build its container at startup.
        AddInitialisable<EmailMetrics>(services);

        AddInitialisable<IntegrationMetrics>(services);
        AddInitialisable<RegistrationMetrics>(services);
        AddInitialisable<BillingMetrics>(services);
        AddInitialisable<AlertPipelineMetrics>(services);
        AddInitialisable<AuthMetrics>(services);
        AddInitialisable<ResilienceMetrics>(services);

        // Injected into the Hangfire duration filter, which is constructed from the container in
        // both processes, so a worker-only registration here would break the API server's Hangfire
        // client. The class creates its gauges only in the worker instead.
        AddObservable<JobMetrics>(services);

        services.AddHostedService<MetricSeriesInitialiser>();

        if (string.IsNullOrWhiteSpace(endpoint) == true)
        {
            return services;
        }

        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: serviceName, serviceVersion: version))
            .WithMetrics(metrics => metrics
                .AddMeter(VordMeter.Name)
                .AddMeter("Npgsql")
                // This also covers the gRPC surface. gRPC calls are ordinary ASP.NET Core endpoints
                // on Kestrel, so they land in http.server.request.duration with the service and
                // method as the route. Grpc.AspNetCore.Server publishes no Meter of its own — its
                // call counters are EventCounters — so naming a gRPC meter here would add a series
                // that never arrives and read like coverage that does not exist. The gRPC status
                // code is not on the request itself either, because a failed call still ends
                // HTTP 200 with its status in a trailer; GrpcStatusMetricTagMiddleware reads that
                // trailer and enriches this same instrument with it.
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter((exporter, _) =>
                {
                    exporter.Endpoint = new Uri(endpoint);
                }));

        return services;
    }

    /// <summary>
    /// Registers a metric class as a singleton and hands the startup pass the same instance to
    /// pre-record its closed-enum series on. Aliasing the wrong concrete type here is invisible at a
    /// glance and would silently drop a class's series, so the type is named once.
    /// </summary>
    private static void AddInitialisable<TMetrics>(IServiceCollection services)
        where TMetrics : class, IInitialisableMetrics
    {
        services.AddSingleton<TMetrics>();
        services.AddSingleton<IInitialisableMetrics>(provider => provider.GetRequiredService<TMetrics>());
    }

    /// <summary>
    /// Registers a gauge-backed metric class as a singleton alongside the descriptor that makes the
    /// startup pass construct it. Without the descriptor the class is built only if something else
    /// happens to inject it, and its series would quietly never exist.
    /// </summary>
    private static void AddObservable<TMetrics>(IServiceCollection services)
        where TMetrics : class, IObservableMetrics
    {
        services.AddSingleton<TMetrics>();
        services.AddSingleton(ObservableMetricsDescriptor.For<TMetrics>());
    }

    /// <summary>
    /// The endpoint to export to, preferring this application's own setting and falling back to the
    /// standard OpenTelemetry variable so a self-hoster can point the deployment at their own
    /// collector without a code change. Gating on the application setting alone would mean the
    /// exporter is never registered, and so never reads the standard variable itself.
    /// </summary>
    private static string? ResolveEndpoint(IConfiguration configuration)
    {
        string? configured = configuration["Observability:OtlpEndpoint"];
        if (string.IsNullOrWhiteSpace(configured) == false)
        {
            return configured;
        }

        string? standard = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        return string.IsNullOrWhiteSpace(standard) ? null : standard;
    }

    private static string ResolveServiceName(ObservabilityHost host)
    {
        return host switch
        {
            ObservabilityHost.ApiServer => "api-server",
            ObservabilityHost.ServicesWorker => "services-worker",
            _ => throw new ArgumentOutOfRangeException(nameof(host), host, "Unknown observability host."),
        };
    }
}
