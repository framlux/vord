// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Extensions;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Proves the worker container can construct the Kubernetes projection: a hosted service that cannot
/// be resolved fails the host's start, which would take the state-streaming services down with it.
/// </summary>
public sealed class KubernetesInventoryProjectionRegistrationTests
{
    private static ServiceProvider BuildWorkerProvider()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<ObjectStorageOptions>();
        services.AddOptions<DeploymentOptions>();
        services.AddSingleton<DeploymentMode>();
        services.AddSingleton(Substitute.For<ISqlDialect>());
        services.AddSingleton(Substitute.For<IAdvisoryLockProvider>());

        services.AddCoreObservability(configuration, ObservabilityHost.ServicesWorker);
        services.AddBackgroundWorkers(isSaas: false, new ObjectStorageOptions(), configuration);

        return services.BuildServiceProvider();
    }

    [Test]
    public async Task WorkerHost_ResolvesTheProjectionAsAHostedService()
    {
        using ServiceProvider provider = BuildWorkerProvider();

        IHostedService[] projections = [.. provider.GetServices<IHostedService>().OfType<KubernetesInventoryProjectionService>()];

        await Assert.That(projections.Length).IsEqualTo(1);
    }

    [Test]
    public async Task WorkerHost_PreRecordsTheProjectionFailureSeries()
    {
        using ServiceProvider provider = BuildWorkerProvider();

        IInitialisableMetrics[] initialisable = [.. provider.GetServices<IInitialisableMetrics>()];

        await Assert.That(initialisable.OfType<KubernetesInventoryMetrics>().Count()).IsEqualTo(1);
    }
}
