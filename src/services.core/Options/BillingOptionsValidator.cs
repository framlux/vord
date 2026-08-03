// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Validates <see cref="BillingOptions"/> configuration.
/// When billing is enabled, the gRPC URL must be provided.
/// </summary>
public sealed class BillingOptionsValidator : IValidateOptions<BillingOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, BillingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Enabled && string.IsNullOrWhiteSpace(options.GrpcUrl))
        {
            return ValidateOptionsResult.Fail("Billing:GrpcUrl is required when Billing:Enabled is true.");
        }

        // A client certificate is useless without its key, and configuring one without the other
        // would silently fall back to an unauthenticated channel the billing API then rejects.
        bool hasCertificate = string.IsNullOrWhiteSpace(options.ClientCertificatePath) == false;
        bool hasKey = string.IsNullOrWhiteSpace(options.ClientCertificateKeyPath) == false;
        if (hasCertificate != hasKey)
        {
            return ValidateOptionsResult.Fail(
                "Billing:ClientCertificatePath and Billing:ClientCertificateKeyPath must be set together.");
        }

        return ValidateOptionsResult.Success;
    }
}
