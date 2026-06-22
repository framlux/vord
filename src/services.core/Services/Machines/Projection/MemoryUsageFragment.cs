// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>MemoryUsage-derived summary and detail values.</summary>
/// <param name="MemoryUsagePercent">The memory usage percentage (summary column).</param>
/// <param name="MemoryUsedBytes">The memory used in bytes (detail column).</param>
internal sealed record MemoryUsageFragment(int? MemoryUsagePercent, long? MemoryUsedBytes);
