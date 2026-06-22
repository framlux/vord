// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>HardwareHealth-derived summary flags and detail payload.</summary>
/// <param name="HasDiskHealthIssue">Whether any disk reported a health issue (summary column).</param>
/// <param name="HasHardwareIssue">Whether any hardware component reported an issue (summary column).</param>
/// <param name="HardwareHealth">The raw hardware-health JSON payload (detail column).</param>
internal sealed record HardwareHealthFragment(bool HasDiskHealthIssue, bool HasHardwareIssue, string HardwareHealth);
