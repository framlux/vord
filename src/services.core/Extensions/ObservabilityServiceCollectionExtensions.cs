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

        if (host == ObservabilityHost.ApiServer)
        {
            services.AddSingleton<IngestMetrics>();
            services.AddSingleton<IInitialisableMetrics>(provider => provider.GetRequiredService<IngestMetrics>());
        }

        // Registered in both processes because AddCoreServices registers AlertDeliveryService
        // unconditionally on both. A host-conditional registration here would leave one of them
        // unable to build its container at startup.
        services.AddSingleton<EmailMetrics>();
        services.AddSingleton<IInitialisableMetrics>(provider => provider.GetRequiredService<EmailMetrics>());

        services.AddSingleton<IntegrationMetrics>();
        services.AddSingleton<IInitialisableMetrics>(provider => provider.GetRequiredService<IntegrationMetrics>());

        services.AddSingleton<RegistrationMetrics>();
        services.AddSingleton<IInitialisableMetrics>(provider => provider.GetRequiredService<RegistrationMetrics>());

        services.AddSingleton<BillingMetrics>();
        services.AddSingleton<IInitialisableMetrics>(provider => provider.GetRequiredService<BillingMetrics>());

        services.AddSingleton<AlertPipelineMetrics>();
        services.AddSingleton<IInitialisableMetrics>(provider => provider.GetRequiredService<AlertPipelineMetrics>());

        services.AddSingleton<AuthMetrics>();
        services.AddSingleton<IInitialisableMetrics>(provider => provider.GetRequiredService<AuthMetrics>());

        services.AddSingleton<ResilienceMetrics>();
        services.AddSingleton<IInitialisableMetrics>(provider => provider.GetRequiredService<ResilienceMetrics>());

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
