// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database;

/// <summary>
/// The write-time policy that maps a tenant's effective retention days to a physical
/// <see cref="RetentionClass"/>, together with the fixed per-class drop windows. Declared next to the
/// enum so the stamping path (telemetry ingest) and the partition-maintenance path (drop scheduling)
/// share one source of truth. The Short and Medium windows are fixed constants; the Long window has a
/// floor here (<see cref="LongWindowDays"/>) that the partition job may extend for a rare
/// override that grants retention beyond 365 days — one tenant's override can never stretch another
/// class's physical window.
/// </summary>
public static class RetentionClassPolicy
{
    /// <summary>
    /// The fixed drop window, in days, of the <see cref="RetentionClass.Short"/> class. Partitions in
    /// this class are dropped once they are older than this window (plus the job's safety buffer).
    /// </summary>
    public const int ShortWindowDays = 1;

    /// <summary>
    /// The fixed drop window, in days, of the <see cref="RetentionClass.Medium"/> class.
    /// </summary>
    public const int MediumWindowDays = 60;

    /// <summary>
    /// The floor drop window, in days, of the <see cref="RetentionClass.Long"/> class. The effective
    /// Long window is the greater of this floor and the largest per-tenant retention override, so a
    /// rare over-365-day override extends only the Long class.
    /// </summary>
    public const int LongWindowDays = 365;

    /// <summary>
    /// Days of future daily partitions the partition-maintenance job pre-creates ahead of server time.
    /// This is the single source of truth for that horizon: the ingest path clamps a row's
    /// partition-key timestamp to it so a derived timestamp always lands in an existing partition, and
    /// the reclassification job extends its day walk by it so clock-skewed, future-dated rows move with
    /// the rest. Kept here, beside the class windows, so the three consumers cannot drift apart.
    /// </summary>
    public const int PartitionCreateAheadDays = 7;

    /// <summary>
    /// Maps a tenant's effective retention days to the smallest <see cref="RetentionClass"/> whose
    /// window can physically hold the data. A non-positive or unknown value fails safe to
    /// <see cref="RetentionClass.Short"/>, the cheapest class, so a resolution glitch can never route a
    /// row into a longer-lived partition than the plan allows.
    /// </summary>
    /// <param name="effectiveRetentionDays">The tenant's effective retention, in days.</param>
    /// <returns>The retention class the row must be stamped with.</returns>
    public static RetentionClass Classify(int effectiveRetentionDays)
    {
        if (effectiveRetentionDays <= ShortWindowDays)
        {
            return RetentionClass.Short;
        }

        if (effectiveRetentionDays <= MediumWindowDays)
        {
            return RetentionClass.Medium;
        }

        return RetentionClass.Long;
    }
}
