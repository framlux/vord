// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Deployment;

/// <summary>
/// Singleton read surface for the deployment mode. Consumers ask this rather than reading
/// configuration so there is exactly one place the mode is interpreted.
/// </summary>
public sealed class DeploymentMode
{
    /// <summary>
    /// Whether this process is running as a self-hosted deployment: no billing, no internal
    /// control plane, no tier limits, SMTP email.
    /// </summary>
    public bool IsSelfHosted { get; }

    /// <summary>
    /// Whether this process is running as the hosted SaaS deployment. The exact negation of
    /// <see cref="IsSelfHosted"/>; there is deliberately no third state.
    /// </summary>
    public bool IsSaas => IsSelfHosted == false;

    /// <summary>
    /// Creates a new instance of the <see cref="DeploymentMode"/> class.
    /// </summary>
    /// <param name="deploymentOptions">The bound deployment configuration.</param>
    public DeploymentMode(IOptions<DeploymentOptions> deploymentOptions)
    {
        ArgumentNullException.ThrowIfNull(deploymentOptions);

        IsSelfHosted = deploymentOptions.Value.SelfHosted;
    }
}
