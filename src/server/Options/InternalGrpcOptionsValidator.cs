// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Fails startup when the internal mutual-TLS endpoint is switched on but incompletely
/// configured. A half-configured endpoint would either fail to bind or, worse, accept callers
/// it should not, so the process refuses to start instead.
/// </summary>
public sealed class InternalGrpcOptionsValidator : IValidateOptions<InternalGrpcOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, InternalGrpcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Enabled == false)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = new();

        if ((options.Port < 1) || (options.Port > 65535))
        {
            failures.Add("InternalGrpc:Port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            failures.Add("InternalGrpc:CertificatePath is required when InternalGrpc:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(options.CertificateKeyPath))
        {
            failures.Add("InternalGrpc:CertificateKeyPath is required when InternalGrpc:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(options.ClientCaPath))
        {
            failures.Add("InternalGrpc:ClientCaPath is required when InternalGrpc:Enabled is true.");
        }

        if (options.AllowedClientSubjects.Count == 0)
        {
            failures.Add(
                "InternalGrpc:AllowedClientSubjects must list at least one subject when " +
                "InternalGrpc:Enabled is true — otherwise any certificate issued by the internal " +
                "CA would be accepted.");
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
    }
}
