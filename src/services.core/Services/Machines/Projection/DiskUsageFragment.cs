// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>DiskUsage-derived summary value and detail payload.</summary>
/// <param name="MaxDiskUsagePercent">The maximum disk usage percentage across all disks (summary column).</param>
/// <param name="DiskUsages">The raw disk-usage JSON payload (detail column).</param>
internal sealed record DiskUsageFragment(int MaxDiskUsagePercent, string DiskUsages);
