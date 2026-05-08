// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;

/// <summary>
/// Result of validating and resolving a history time range.
/// </summary>
public enum HistoryRangeResult
{
    /// <summary>Range is valid and within retention.</summary>
    Ok,

    /// <summary>Range string is not recognized.</summary>
    InvalidRange,

    /// <summary>Range exceeds the tenant's retention limit.</summary>
    RetentionExceeded
}
