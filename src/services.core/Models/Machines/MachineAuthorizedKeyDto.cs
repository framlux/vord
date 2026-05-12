// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Machines;

/// <summary>
/// Data transfer object for a machine authorized key, including display information.
/// </summary>
public sealed class MachineAuthorizedKeyDto
{
    /// <summary>
    /// The unique identifier for the authorization record.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The signing key ID that was authorized.
    /// </summary>
    public int SigningKeyId { get; set; }

    /// <summary>
    /// The user-chosen label for the signing key.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// The SHA-256 fingerprint of the signing key.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>
    /// The username of the signing key owner.
    /// </summary>
    public string OwnerUsername { get; set; } = string.Empty;

    /// <summary>
    /// The timestamp when the authorization was granted.
    /// </summary>
    public DateTimeOffset AuthorizedAt { get; set; }

    /// <summary>
    /// The username of the user who granted the authorization.
    /// </summary>
    public string AuthorizedByUsername { get; set; } = string.Empty;

    /// <summary>
    /// The timestamp when the authorization was revoked, if applicable.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Whether this authorization is currently active.
    /// </summary>
    public bool IsActive { get; set; }
}
