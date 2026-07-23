// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Lifecycle state of a tenant deletion. One row per deletion in <c>TenantDeletions</c>.
/// </summary>
public enum TenantDeletionStatus : short
{
    /// <summary>Phase 1 complete: tenant deactivated, awaiting the scheduled purge.</summary>
    Deactivated = 1,

    /// <summary>Phase 2 complete: operational and personal data purged, identity skeleton masked.</summary>
    Purged = 2,

    /// <summary>Operator canceled the deletion during the grace window; tenant reactivated.</summary>
    Restored = 3,
}
