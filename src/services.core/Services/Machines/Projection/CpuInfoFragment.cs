// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>CpuInfo-derived detail values.</summary>
/// <param name="CpuType">The CPU type string (detail column).</param>
/// <param name="CpuPhysicalCpus">The number of physical CPUs (detail column).</param>
/// <param name="CpuLogicalCpus">The number of logical CPUs (detail column).</param>
internal sealed record CpuInfoFragment(string? CpuType, int? CpuPhysicalCpus, int? CpuLogicalCpus);
