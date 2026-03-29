// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Configuration options for TLS certificate paths.
/// </summary>
public sealed class CertificateOptions
{
    /// <summary>
    /// The file path to the root certificate used for agent certificate signing.
    /// </summary>
    public string RootCertPath { get; set; } = "/mnt/framlux/fleet/certs/root.pfx";
}
