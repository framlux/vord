// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Deployment;

/// <summary>
/// Emits a startup warning when billing is configured but the deployment mode means it will be
/// ignored. The mistake this catches is a hosted deployment that loses its Deployment:SelfHosted
/// setting: it would otherwise boot as self-hosted with a full billing configuration present and
/// report nothing, silently serving the product without billing.
/// </summary>
/// <remarks>
/// This lives in a hosted service rather than in the <see cref="DeploymentMode"/> constructor
/// deliberately. That type is a pure value holder built by hand in each entry point before the
/// container exists, so it has no logger to write to and no way to acquire one.
/// </remarks>
public sealed class IgnoredBillingConfigurationWarning : IHostedService
{
    private readonly DeploymentMode _deploymentMode;
    private readonly BillingOptions _billingOptions;
    private readonly ILogger<IgnoredBillingConfigurationWarning> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="IgnoredBillingConfigurationWarning"/> class.
    /// </summary>
    /// <param name="deploymentMode">The resolved deployment mode.</param>
    /// <param name="billingOptions">The bound billing configuration, read only to detect presence.</param>
    /// <param name="logger">Logger the warning is written to.</param>
    public IgnoredBillingConfigurationWarning(
        DeploymentMode deploymentMode,
        IOptions<BillingOptions> billingOptions,
        ILogger<IgnoredBillingConfigurationWarning> logger)
    {
        ArgumentNullException.ThrowIfNull(deploymentMode);
        ArgumentNullException.ThrowIfNull(billingOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _deploymentMode = deploymentMode;
        _billingOptions = billingOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_deploymentMode.IsSelfHosted && (string.IsNullOrWhiteSpace(_billingOptions.GrpcUrl) == false))
        {
            _logger.LogWarning(
                "Billing is configured but will be ignored because this is a self-hosted deployment. If this is the hosted deployment, Deployment:SelfHosted must be set to false.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
