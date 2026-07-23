// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.CodeAnalysis;

namespace Framlux.FleetManagement.Database;

/// <summary>
/// The physical retention class a telemetry row is stamped with at write time. The class is the
/// LIST partition key of <c>MachineTelemetry</c>; each class owns its own set of daily range
/// partitions that are dropped on the class's own schedule, so a tenant's data physically lives no
/// longer than its plan allows. The class is derived from the tenant's <em>effective</em> retention
/// days (tier default overridden by any per-tenant override), not from the tier name, so an override
/// tenant lands in the smallest class whose window can physically hold its data.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Short/Medium/Long are the owner-approved retention-class names and are used verbatim as the physical partition parent names (MachineTelemetry_Short/Medium/Long); renaming them would break that contract.")]
public enum RetentionClass : short
{
    /// <summary>
    /// Short-lived telemetry (effective retention of at most one day). Standard home of the Free tier,
    /// whose partitions are dropped after a one-day window.
    /// </summary>
    Short = 0,

    /// <summary>
    /// Medium-lived telemetry (effective retention of at most sixty days). Standard home of the Pro
    /// tier, whose partitions are dropped after a sixty-day window.
    /// </summary>
    Medium = 1,

    /// <summary>
    /// Long-lived telemetry (effective retention beyond sixty days). Standard home of the Team tier,
    /// whose partitions are dropped after a window of at least 365 days, extended only when a rare
    /// per-tenant override grants retention beyond that floor.
    /// </summary>
    Long = 2,
}
