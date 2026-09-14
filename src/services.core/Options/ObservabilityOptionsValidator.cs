// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Validates observability configuration. An absent endpoint is valid and means "do not export";
/// a present endpoint must be usable, because a malformed one would fail later inside the exporter
/// where the cause is much harder to see.
/// </summary>
public sealed class ObservabilityOptionsValidator : IValidateOptions<ObservabilityOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.IsExportEnabled == false)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.ServiceName) == true)
        {
            return ValidateOptionsResult.Fail(
                "Observability:ServiceName is required when Observability:OtlpEndpoint is set.");
        }

        // An absolute URI alone is too weak a check: "collector:4317" parses as absolute, with
        // "collector" read as the scheme. The exporter speaks OTLP over http or https and nothing
        // else, so requiring the scheme is what actually rejects a host:port typed without one.
        if (Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out Uri? endpoint) == false)
        {
            return ValidateOptionsResult.Fail(
                $"Observability:OtlpEndpoint must be an absolute URI. Got '{options.OtlpEndpoint}'.");
        }

        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) == false &&
            string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) == false)
        {
            return ValidateOptionsResult.Fail(
                $"Observability:OtlpEndpoint must use http or https. Got '{options.OtlpEndpoint}'.");
        }

        return ValidateOptionsResult.Success;
    }
}
