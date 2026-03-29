// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the status of a tenant invitation.
/// </summary>
public enum InvitationStatus : int
{
    /// <summary>No invitation status.</summary>
    None = 0,
    /// <summary>Invitation is pending acceptance.</summary>
    Pending = 1,
    /// <summary>Invitation has been accepted.</summary>
    Accepted = 2,
    /// <summary>Invitation has been revoked by an admin.</summary>
    Revoked = 3,
    /// <summary>Invitation has expired.</summary>
    Expired = 4
}
