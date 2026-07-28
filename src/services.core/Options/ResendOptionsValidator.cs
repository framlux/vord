// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Validates <see cref="ResendOptions"/> configuration.
/// Email is optional: with no API key the service skips sending entirely, which is a supported
/// deployment. But a configured API key with no sender address is not a working configuration —
/// Resend rejects any send whose From address is on an unverified domain, and the rejection is
/// only visible in logs, so the failure mode is invitations that silently never arrive. Failing at
/// startup turns that into an obvious misconfiguration instead.
/// </summary>
public sealed class ResendOptionsValidator : IValidateOptions<ResendOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, ResendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.FromEmail))
        {
            return ValidateOptionsResult.Fail(
                "Resend:FromEmail is required when Resend:ApiKey is configured. It must be an address on a domain verified in Resend, or every send is rejected.");
        }

        return ValidateOptionsResult.Success;
    }
}
