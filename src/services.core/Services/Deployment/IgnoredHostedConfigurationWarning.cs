// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Deployment;

/// <summary>
/// Emits a startup warning when hosted-only configuration is present but the deployment mode means
/// it will be ignored. The mistake this catches is a hosted deployment that loses its
/// Deployment:SelfHosted setting: it would otherwise boot as self-hosted with a full hosted
/// configuration and report nothing.
/// </summary>
/// <remarks>
/// <para>
/// Two subsystems go quiet together when that flag is lost, and both fail silently rather than
/// loudly. Billing resolves the no-op client, so the product is served without anyone being
/// charged. Email resolves the no-op transport — a hosted configuration carries a Resend key and no
/// SMTP host — and every send then reports Skipped, which callers are required to treat as terminal
/// success and never retry, so invitations and alerts are discarded without a single error.
/// </para>
/// <para>
/// This lives in a hosted service rather than in the <see cref="DeploymentMode"/> constructor
/// deliberately. That type is a pure value holder built by hand in each entry point before the
/// container exists, so it has no logger to write to and no way to acquire one.
/// </para>
/// </remarks>
public sealed class IgnoredHostedConfigurationWarning : IHostedService
{
    private readonly DeploymentMode _deploymentMode;
    private readonly BillingOptions _billingOptions;
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<IgnoredHostedConfigurationWarning> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="IgnoredHostedConfigurationWarning"/> class.
    /// </summary>
    /// <param name="deploymentMode">The resolved deployment mode.</param>
    /// <param name="billingOptions">The bound billing configuration, read only to detect presence.</param>
    /// <param name="emailOptions">The bound email configuration, read only to detect presence.</param>
    /// <param name="logger">Logger the warnings are written to.</param>
    public IgnoredHostedConfigurationWarning(
        DeploymentMode deploymentMode,
        IOptions<BillingOptions> billingOptions,
        IOptions<EmailOptions> emailOptions,
        ILogger<IgnoredHostedConfigurationWarning> logger)
    {
        ArgumentNullException.ThrowIfNull(deploymentMode);
        ArgumentNullException.ThrowIfNull(billingOptions);
        ArgumentNullException.ThrowIfNull(emailOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _deploymentMode = deploymentMode;
        _billingOptions = billingOptions.Value;
        _emailOptions = emailOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_deploymentMode.IsSaas)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_billingOptions.GrpcUrl) == false)
        {
            _logger.LogWarning(
                "Billing is configured but will be ignored because this is a self-hosted deployment. If this is the hosted deployment, Deployment:SelfHosted must be set to false.");
        }

        // Reported separately from billing: a deployment can plausibly carry one without the other,
        // and losing email is the quieter of the two failures — no send ever reports an error.
        if (string.IsNullOrWhiteSpace(_emailOptions.Resend.ApiKey) == false &&
            string.IsNullOrWhiteSpace(_emailOptions.Smtp.Host))
        {
            _logger.LogWarning(
                "A Resend API key is configured but will be ignored because this is a self-hosted deployment, and no SMTP host is set — every email will be skipped silently. If this is the hosted deployment, Deployment:SelfHosted must be set to false.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
