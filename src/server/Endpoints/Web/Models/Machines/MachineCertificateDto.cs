// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;

/// <summary>
/// Certificate data returned to the UI.
/// </summary>
public sealed class MachineCertificateDto
{
    /// <summary>
    /// The certificate record ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The SHA-256 thumbprint of the certificate.
    /// </summary>
    public string Thumbprint { get; set; } = string.Empty;

    /// <summary>
    /// When the certificate was issued.
    /// </summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>
    /// When the certificate expires.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When the certificate was revoked, or null if still active.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Whether the certificate is currently active (not revoked and not expired).
    /// </summary>
    public bool IsActive { get; set; }
}
