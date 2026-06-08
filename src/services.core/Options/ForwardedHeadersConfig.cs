// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Configuration for trusted reverse-proxy networks. Used to populate
/// <see cref="Microsoft.AspNetCore.Builder.ForwardedHeadersOptions.KnownNetworks"/> so that
/// X-Forwarded-* headers from the configured CIDRs are honored. Without this, ASP.NET Core
/// only accepts forwarded headers from loopback, which silently breaks rate limiting and
/// audit-log IP capture behind a Kubernetes ingress.
/// </summary>
public sealed class ForwardedHeadersConfig
{
    /// <summary>
    /// CIDR notation list of networks from which X-Forwarded-* headers may be trusted.
    /// Example: <c>["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]</c>.
    /// In <c>Production</c> the list MUST be non-empty; an empty list throws at startup.
    /// </summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>
    /// Maximum number of forwarded header chain entries to honor. Defaults to <c>2</c>
    /// (allows one trusted proxy plus an SSL-terminating LB).
    /// </summary>
    public int ForwardLimit { get; set; } = 2;
}
