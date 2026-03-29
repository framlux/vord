// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Result of resending an invitation.
/// </summary>
public sealed class InvitationResendResult
{
    /// <summary>
    /// The new invitation ID.
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// The invited email address.
    /// </summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>
    /// The new invitation token.
    /// </summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>
    /// The URL to accept the invitation.
    /// </summary>
    public string AcceptUrl { get; init; } = string.Empty;

    /// <summary>
    /// When the invitation expires.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The invitation status.
    /// </summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
