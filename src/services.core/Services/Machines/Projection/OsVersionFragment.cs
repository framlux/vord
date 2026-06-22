// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>OsVersion-derived summary and detail values.</summary>
/// <param name="OsName">The operating system name (summary column).</param>
/// <param name="OsVersion">The operating system version (summary column).</param>
/// <param name="Kernel">The kernel version string (detail column).</param>
internal sealed record OsVersionFragment(string? OsName, string? OsVersion, string? Kernel);
