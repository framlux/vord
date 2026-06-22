// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>MemoryInfo-derived detail values.</summary>
/// <param name="SwapTotalBytes">The total swap space in bytes (detail column).</param>
/// <param name="SwapFreeBytes">The free swap space in bytes (detail column).</param>
internal sealed record MemoryInfoFragment(long? SwapTotalBytes, long? SwapFreeBytes);
