// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Validates email configuration against the deployment mode. The two modes have opposite rules:
/// self-hosted may run with no email at all, while the hosted deployment always sends invitations
/// and alerts, so a missing key there is a misconfiguration that must stop startup rather than
/// silently disable delivery.
/// </summary>
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    private readonly DeploymentMode _deploymentMode;

    /// <summary>
    /// Creates a new instance of the <see cref="EmailOptionsValidator"/> class.
    /// </summary>
    /// <param name="deploymentMode">The deployment mode this process is running as.</param>
    public EmailOptionsValidator(DeploymentMode deploymentMode)
    {
        _deploymentMode = deploymentMode;
    }

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (_deploymentMode.IsSaas)
        {
            if (string.IsNullOrWhiteSpace(options.Resend.ApiKey))
            {
                failures.Add(
                    "Email:Resend:ApiKey is required when Deployment:SelfHosted is false. The hosted deployment always sends invitations and alerts, so a missing key is a misconfiguration rather than a deployment style.");
            }

            if (string.IsNullOrWhiteSpace(options.FromEmail))
            {
                failures.Add(
                    "Email:FromEmail is required when Deployment:SelfHosted is false. It must be an address on a domain verified in Resend, or every send is rejected.");
            }
        }
        else if (string.IsNullOrWhiteSpace(options.Smtp.Host) == false)
        {
            if (string.IsNullOrWhiteSpace(options.FromEmail))
            {
                failures.Add("Email:FromEmail is required when Email:Smtp:Host is configured.");
            }

            if ((options.Smtp.Port < 1) || (options.Smtp.Port > 65535))
            {
                failures.Add("Email:Smtp:Port must be between 1 and 65535.");
            }
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
    }
}
