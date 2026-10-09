// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>The shape of a tenant's quota cutoff.</summary>
public enum QuotaCutoffKind
{
    /// <summary>The tenant is within its limit; nothing is paused.</summary>
    None,

    /// <summary>The limit is zero or below; every registered machine is paused.</summary>
    All,

    /// <summary>Machines whose id is greater than the cutoff id are paused.</summary>
    After,
}
