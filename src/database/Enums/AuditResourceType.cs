// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the type of resource affected by an audit log entry.
/// </summary>
public enum AuditResourceType : short
{
    /// <summary>A user account.</summary>
    User = 1,
    /// <summary>A machine.</summary>
    Machine = 2,
    /// <summary>A tenant.</summary>
    Tenant = 3,
    /// <summary>A subscription.</summary>
    Subscription = 4,
    /// <summary>An invitation.</summary>
    Invitation = 5,
    /// <summary>A registration token.</summary>
    RegistrationToken = 6,
    /// <summary>A data export.</summary>
    DataExport = 7,
    /// <summary>A signing key.</summary>
    SigningKey = 8,
    /// <summary>A remote command.</summary>
    RemoteCommand = 9
}
