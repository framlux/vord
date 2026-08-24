// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Configuration options for reaching the SaaS billing API. Ignored entirely when
/// Deployment:SelfHosted is true.
/// </summary>
public sealed class BillingOptions
{
    /// <summary>
    /// The gRPC URL for the billing API service.
    /// </summary>
    public string GrpcUrl { get; set; } = string.Empty;

    /// <summary>
    /// Path to the PEM-encoded client certificate this process presents to the billing API.
    /// The billing API authorises on its subject, so this file is the process's identity.
    /// </summary>
    public string ClientCertificatePath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the PEM-encoded private key matching <see cref="ClientCertificatePath"/>.
    /// </summary>
    public string ClientCertificateKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the PEM-encoded internal certificate authority bundle used to verify the billing
    /// API's server certificate. Left empty, the platform's default trust store is used, which
    /// is correct only for a deployment that terminates TLS elsewhere.
    /// </summary>
    public string ServerCaPath { get; set; } = string.Empty;
}
