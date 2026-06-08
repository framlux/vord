// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Framlux.FleetManagement.Server.Startup;

/// <summary>
/// Configures <see cref="ForwardedHeadersOptions"/> from a <see cref="ForwardedHeadersConfig"/>
/// section. Fails fast in Production when no trusted networks are configured.
/// </summary>
public static class ForwardedHeadersStartup
{
    /// <summary>
    /// Validates the configured CIDR list and applies it to the
    /// <see cref="ForwardedHeadersOptions"/>. Throws on invalid CIDR strings or on an
    /// empty Production configuration.
    /// </summary>
    /// <param name="options">The framework options to populate.</param>
    /// <param name="config">The configured trusted-network list and forward limit.</param>
    /// <param name="environmentName">The hosting environment name; controls fail-fast behavior.</param>
    public static void Configure(ForwardedHeadersOptions options, ForwardedHeadersConfig config, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(environmentName);

        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto |
            ForwardedHeaders.XForwardedHost;

        options.ForwardLimit = config.ForwardLimit;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        bool isProduction = string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
        if (isProduction && (config.KnownNetworks.Length == 0))
        {
            throw new InvalidOperationException(
                "ForwardedHeaders:KnownNetworks must be a non-empty list of CIDR ranges in Production. "
                + "Configure the cluster pod CIDRs the SSL-terminating proxy uses.");
        }

        foreach (string cidr in config.KnownNetworks)
        {
            if (string.IsNullOrWhiteSpace(cidr))
            {
                throw new InvalidOperationException(
                    "ForwardedHeaders:KnownNetworks contains an empty or whitespace entry.");
            }

            IPNetwork parsed;
            try
            {
                parsed = IPNetwork.Parse(cidr);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownNetworks entry '{cidr}' is not a valid CIDR.",
                    ex);
            }

            options.KnownIPNetworks.Add(parsed);
        }
    }
}
