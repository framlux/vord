// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Validators;

/// <summary>
/// Tests for <see cref="DeploymentOptionsValidator"/>. The deployment mode is the single switch
/// between SaaS and self-hosted, so a configuration that contradicts the declared mode must stop
/// the process rather than degrade into the other mode silently.
/// </summary>
public sealed class DeploymentOptionsValidatorTests
{
    private static DeploymentOptionsValidator CreateValidator(
        BillingOptions billing,
        bool internalGrpcEnabled)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalGrpc:Enabled"] = internalGrpcEnabled ? "true" : "false",
            })
            .Build();

        return new DeploymentOptionsValidator(Options.Create(billing), configuration);
    }

    /// <summary>
    /// The zero-configuration case: a fresh clone with nothing set is a valid self-hosted install.
    /// </summary>
    [Test]
    public async Task Validate_DefaultOptions_IsSelfHostedAndSucceeds()
    {
        DeploymentOptions options = new();

        await Assert.That(options.SelfHosted).IsTrue();

        ValidateOptionsResult result = CreateValidator(new BillingOptions(), internalGrpcEnabled: false)
            .Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// SaaS without a billing endpoint is not runnable — the failure would otherwise surface as a
    /// gRPC dial error on the first customer checkout.
    /// </summary>
    [Test]
    public async Task Validate_SaasWithoutBillingGrpcUrl_Fails()
    {
        ValidateOptionsResult result = CreateValidator(new BillingOptions(), internalGrpcEnabled: false)
            .Validate(null, new DeploymentOptions { SelfHosted = false });

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Billing:GrpcUrl");
    }

    /// <summary>
    /// SaaS with a billing endpoint is the production configuration.
    /// </summary>
    [Test]
    public async Task Validate_SaasWithBillingGrpcUrl_Succeeds()
    {
        BillingOptions billing = new() { GrpcUrl = "https://billing-api.internal:12237" };

        ValidateOptionsResult result = CreateValidator(billing, internalGrpcEnabled: true)
            .Validate(null, new DeploymentOptions { SelfHosted = false });

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// The mutual-TLS control-plane listener serves only billing and fleet-admin, neither of which
    /// is mapped in self-hosted, so an enabled listener there is a misconfiguration.
    /// </summary>
    [Test]
    public async Task Validate_SelfHostedWithInternalGrpcEnabled_Fails()
    {
        ValidateOptionsResult result = CreateValidator(new BillingOptions(), internalGrpcEnabled: true)
            .Validate(null, new DeploymentOptions { SelfHosted = true });

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("InternalGrpc:Enabled");
    }

    /// <summary>
    /// A leftover Billing section in self-hosted is inert, not fatal — flipping modes to test must
    /// not require gutting the configuration file.
    /// </summary>
    [Test]
    public async Task Validate_SelfHostedWithPopulatedBillingSection_Succeeds()
    {
        BillingOptions billing = new() { GrpcUrl = "https://billing-api.internal:12237" };

        ValidateOptionsResult result = CreateValidator(billing, internalGrpcEnabled: false)
            .Validate(null, new DeploymentOptions { SelfHosted = true });

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// Null options is a programming error, not a configuration error.
    /// </summary>
    [Test]
    public async Task Validate_NullOptions_Throws()
    {
        DeploymentOptionsValidator validator = CreateValidator(new BillingOptions(), internalGrpcEnabled: false);

        await Assert.That(() => validator.Validate(null, null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullBillingOptions_Throws()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        await Assert.That(() => new DeploymentOptionsValidator(null!, configuration))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullConfiguration_Throws()
    {
        await Assert.That(() => new DeploymentOptionsValidator(Options.Create(new BillingOptions()), null!))
            .Throws<ArgumentNullException>();
    }
}
