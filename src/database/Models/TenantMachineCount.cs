// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// SQL grouping projection mapping a tenant to its active machine count. Used so the
/// per-tenant machine count is computed by the database rather than by materializing
/// every machine row and grouping in memory.
/// </summary>
public sealed class TenantMachineCount
{
    /// <summary>The tenant identifier.</summary>
    public int TenantId { get; set; }

    /// <summary>The number of active (non-deleted) machines for the tenant.</summary>
    public int Count { get; set; }
}
