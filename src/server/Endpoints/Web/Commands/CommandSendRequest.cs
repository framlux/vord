// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Commands;

/// <summary>
/// Request to submit a signed remote command.
/// </summary>
public sealed class CommandSendRequest
{
    /// <summary>
    /// Client-generated UUID for the command.
    /// </summary>
    public required string CommandId { get; set; }

    /// <summary>
    /// The target machine ID.
    /// </summary>
    public required long MachineId { get; set; }

    /// <summary>
    /// The signing key ID used to sign the command.
    /// </summary>
    public required int SigningKeyId { get; set; }

    /// <summary>
    /// The type of command (e.g., "reboot", "kill_process").
    /// </summary>
    public required string CommandType { get; set; }

    /// <summary>
    /// JSON-encoded command parameters.
    /// </summary>
    public string? Params { get; set; }

    /// <summary>
    /// Unique nonce for replay prevention (hex-encoded 16 random bytes).
    /// </summary>
    public required string Nonce { get; set; }

    /// <summary>
    /// Base64-encoded Ed25519 signature over the canonical payload.
    /// </summary>
    public required string Signature { get; set; }

    /// <summary>
    /// The exact canonical JSON payload that was signed.
    /// </summary>
    public required string CanonicalPayload { get; set; }

    /// <summary>
    /// ISO 8601 timestamp from the signed payload.
    /// </summary>
    public required DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// ISO 8601 expiry time from the signed payload.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; set; }
}
