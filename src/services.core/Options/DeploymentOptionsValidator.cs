// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Validates that the rest of the configuration agrees with the declared deployment mode. A
/// deployment that declares one mode while configuring the other would otherwise start and behave
/// as the mode nobody intended, which is the failure this whole flag exists to remove.
/// </summary>
public sealed class DeploymentOptionsValidator : IValidateOptions<DeploymentOptions>
{
    private readonly IOptions<BillingOptions> _billingOptions;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Creates a new instance of the <see cref="DeploymentOptionsValidator"/> class.
    /// </summary>
    /// <param name="billingOptions">The bound billing configuration.</param>
    /// <param name="configuration">
    /// Raw configuration, used to read InternalGrpc:Enabled. That section is bound in the server
    /// project because its remaining fields are certificate paths that only the server uses, so it
    /// is not resolvable as typed options from this shared library.
    /// </param>
    public DeploymentOptionsValidator(IOptions<BillingOptions> billingOptions, IConfiguration configuration)
    {
        _billingOptions = billingOptions;
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, DeploymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (options.SelfHosted == false)
        {
            if (string.IsNullOrWhiteSpace(_billingOptions.Value.GrpcUrl))
            {
                failures.Add(
                    "Billing:GrpcUrl is required when Deployment:SelfHosted is false. A SaaS deployment without a reachable billing API cannot complete a checkout.");
            }
        }
        else
        {
            bool internalGrpcEnabled = _configuration.GetValue<bool>("InternalGrpc:Enabled");
            if (internalGrpcEnabled)
            {
                failures.Add(
                    "InternalGrpc:Enabled must be false when Deployment:SelfHosted is true. The mutual-TLS control plane serves only BillingGateway and FleetAdmin, neither of which is mapped in a self-hosted deployment.");
            }
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
    }
}
