// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Validates the per-tier defaults that the hosted deployment depends on. These are plain integers
/// with a zero default, and zero does not fail loudly — it reads as "no limit". A section that
/// silently failed to bind would therefore disable an entitlement rather than break, which is the
/// one outcome worth refusing to start over. Self-hosted deployments have no tiers and are exempt.
/// </summary>
public sealed class TierDefaultOptionsValidator : IValidateOptions<TierDefaultOptions>
{
    private readonly DeploymentMode _deploymentMode;

    /// <summary>
    /// Creates a new instance of the <see cref="TierDefaultOptionsValidator"/> class.
    /// </summary>
    /// <param name="deploymentMode">The deployment mode this process is running as.</param>
    public TierDefaultOptionsValidator(DeploymentMode deploymentMode)
    {
        ArgumentNullException.ThrowIfNull(deploymentMode);

        _deploymentMode = deploymentMode;
    }

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, TierDefaultOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_deploymentMode.IsSaas == false)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];

        ValidateCooldown("Free", options.Free, failures);
        ValidateCooldown("Pro", options.Pro, failures);
        ValidateCooldown("Team", options.Team, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateCooldown(string tier, TierLimitDefaults limits, List<string> failures)
    {
        if (limits.DataExportCooldownHours <= 0)
        {
            failures.Add(
                $"TierDefaults:{tier}:DataExportCooldownHours must be greater than zero when Deployment:SelfHosted is false. A value of zero lets a tenant regenerate a full-database export in an unbounded loop.");
        }
    }
}
