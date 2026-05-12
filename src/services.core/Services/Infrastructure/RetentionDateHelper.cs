// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Provides date-bound calculation for partition-based retention.
/// All queries on partitioned tables should use this to compute the tenant-scoped
/// retention cutoff, enabling PostgreSQL partition pruning.
/// </summary>
public static class RetentionDateHelper
{
    /// <summary>
    /// Computes the retention cutoff for a tenant's subscription.
    /// Rows with timestamps before this value are outside the tenant's retention window.
    /// </summary>
    /// <param name="retentionDays">The tenant's retention days from their subscription.</param>
    /// <returns>The earliest timestamp that should be visible to this tenant.</returns>
    public static DateTimeOffset GetRetentionCutoff(int retentionDays)
    {
        return DateTimeOffset.UtcNow.AddDays(-retentionDays);
    }
}
