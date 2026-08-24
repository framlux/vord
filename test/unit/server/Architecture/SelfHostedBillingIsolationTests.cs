// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Extensions;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.Vord.BillingGrpc;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.UnitTest.Architecture;

/// <summary>
/// A self-hosted deployment must not be able to reach the billing control plane at all. The
/// sibling BillingContractBoundaryTests enforces which code may reference the contract; this one
/// enforces that a self-hosted container never resolves a live client for it, so an accidental
/// registration cannot quietly reintroduce a Stripe dependency into the open-core product.
/// </summary>
public sealed class SelfHostedBillingIsolationTests
{
    private static ServiceCollection BuildServices(bool selfHosted)
    {
        ServiceCollection services = new();
        DeploymentMode mode = new(Microsoft.Extensions.Options.Options.Create(
            new DeploymentOptions { SelfHosted = selfHosted }));

        services.AddCoreServices(mode, new ObjectStorageOptions(), new EmailOptions(), new BillingOptions
        {
            GrpcUrl = "https://billing-api.invalid:12237",
        });

        return services;
    }

    [Test]
    public async Task SelfHosted_DoesNotRegisterBillingManagementClient()
    {
        ServiceCollection services = BuildServices(selfHosted: true);

        bool registered = services.Any(d => d.ServiceType == typeof(BillingManagement.BillingManagementClient));

        await Assert.That(registered).IsFalse();
    }

    [Test]
    public async Task SelfHosted_ResolvesNoOpBillingApiClient()
    {
        ServiceCollection services = BuildServices(selfHosted: true);

        ServiceDescriptor descriptor = services.Single(d => d.ServiceType == typeof(IBillingApiClient));

        await Assert.That(descriptor.ImplementationType).IsEqualTo(typeof(NoOpBillingApiClient));
    }

    /// <summary>
    /// The mirror assertion: without it, a registration that never fires in either mode would
    /// satisfy the tests above while silently breaking the hosted deployment.
    /// </summary>
    [Test]
    public async Task Saas_RegistersBillingManagementClient()
    {
        ServiceCollection services = BuildServices(selfHosted: false);

        bool registered = services.Any(d => d.ServiceType == typeof(BillingManagement.BillingManagementClient));

        await Assert.That(registered).IsTrue();
    }

    [Test]
    public async Task SelfHosted_DoesNotRegisterBillingWebhookHandler()
    {
        ServiceCollection services = BuildServices(selfHosted: true);

        bool registered = services.Any(d => d.ServiceType == typeof(IBillingWebhookHandler));

        await Assert.That(registered).IsFalse();
    }
}
