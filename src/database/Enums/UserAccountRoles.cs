// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Roles for user accounts
/// </summary>
public enum UserAccountRoles : byte
{
    /// <summary>
    /// No role assigned
    /// </summary>
    None = 0,

    /// <summary>
    /// Tenant administrator role
    /// </summary>
    TenantAdmin = 1,

    /// <summary>
    /// Role with machine admin permissions but not user permissions
    /// </summary>
    MachineAdmin = 2,

    /// <summary>
    /// Viewer role with read-only permissions
    /// </summary>
    Viewer = 3
}
