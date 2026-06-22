// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>DiskInfo-derived detail payload.</summary>
/// <param name="DiskInfos">The raw disk-info JSON payload (detail column).</param>
internal sealed record DiskInfoFragment(string DiskInfos);
