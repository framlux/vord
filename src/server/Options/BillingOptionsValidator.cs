// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Validates <see cref="BillingOptions"/> configuration.
/// When billing is enabled, the gRPC URL must be provided.
/// </summary>
public sealed class BillingOptionsValidator : IValidateOptions<BillingOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, BillingOptions options)
    {
        if (options.Enabled && string.IsNullOrWhiteSpace(options.GrpcUrl))
        {
            return ValidateOptionsResult.Fail("Billing:GrpcUrl is required when Billing:Enabled is true.");
        }

        return ValidateOptionsResult.Success;
    }
}
