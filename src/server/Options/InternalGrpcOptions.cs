// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Configuration for the dedicated mutual-TLS gRPC endpoint that serves the internal
/// billing and fleet-admin services. This endpoint is deliberately separate from the agent
/// gRPC endpoint: agents authenticate with an API key and never present a client certificate,
/// so requiring one on the shared port would lock the entire fleet out.
/// </summary>
public sealed class InternalGrpcOptions
{
    /// <summary>
    /// Whether to bind the dedicated mutual-TLS listener. A deployment that runs without the
    /// billing and admin services (self-hosting) leaves this off. When it is off the internal
    /// services still refuse every call, because no caller can present a client certificate on
    /// the plain-text agent endpoint.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// TCP port for the mutual-TLS listener. Must be reachable in-cluster only — it is never
    /// published through an ingress route.
    /// </summary>
    public int Port { get; set; } = 12236;

    /// <summary>
    /// Path to the PEM-encoded server certificate presented on the internal endpoint.
    /// </summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the PEM-encoded private key matching <see cref="CertificatePath"/>.
    /// </summary>
    public string CertificateKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the PEM-encoded certificate authority bundle that client certificates must
    /// chain to. This is the internal CA, never a publicly trusted root.
    /// </summary>
    public string ClientCaPath { get; set; } = string.Empty;

    /// <summary>
    /// The certificate subjects permitted to call the internal services. A caller is accepted
    /// only when its certificate chains to <see cref="ClientCaPath"/> <em>and</em> its identity
    /// appears here — otherwise any certificate the internal CA ever issued would be accepted,
    /// which is no stronger than a shared secret. Each entry is matched against the full subject
    /// distinguished name, the common name, and every DNS subject alternative name.
    /// </summary>
    public IList<string> AllowedClientSubjects { get; set; } = new List<string>();
}
