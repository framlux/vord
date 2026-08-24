// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Settings for the SMTP transport, used by self-hosted deployments. An empty host means email is
/// switched off, which is a supported configuration.
/// </summary>
public sealed class SmtpEmailOptions
{
    /// <summary>
    /// The SMTP server hostname. Empty disables email entirely.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// The SMTP server port. Defaults to the submission port.
    /// </summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// The username for SMTP authentication. Empty sends unauthenticated, which suits a local relay.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The password for SMTP authentication.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Whether to upgrade the connection with STARTTLS. Defaults to true; set false only for a
    /// relay reachable exclusively over a trusted local network.
    /// </summary>
    public bool UseStartTls { get; set; } = true;
}
