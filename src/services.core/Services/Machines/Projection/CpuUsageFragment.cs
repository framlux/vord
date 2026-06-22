// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>CpuUsage-derived summary value.</summary>
/// <param name="CpuUsagePercent">The CPU usage percentage (summary column).</param>
internal sealed record CpuUsageFragment(int? CpuUsagePercent);
