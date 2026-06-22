// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>PackageUpdates-derived summary values.</summary>
/// <param name="PendingUpdates">The number of pending package updates (summary column).</param>
/// <param name="SecurityUpdates">The number of pending security updates (summary column).</param>
internal sealed record PackageUpdatesFragment(int? PendingUpdates, int? SecurityUpdates);
