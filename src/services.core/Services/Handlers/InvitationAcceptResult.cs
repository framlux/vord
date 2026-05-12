// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Result of accepting an invitation.
/// </summary>
public sealed class InvitationAcceptResult
{
    /// <summary>
    /// The tenant ID the user was added to.
    /// </summary>
    public int TenantId { get; init; }

    /// <summary>
    /// Whether a personal tenant was provisioned for the user.
    /// </summary>
    public bool PersonalTenantProvisioned { get; init; }

    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
