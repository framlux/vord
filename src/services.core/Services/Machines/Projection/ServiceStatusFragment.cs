// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>ServiceStatus-derived summary values.</summary>
/// <param name="TotalServices">The total number of services (summary column).</param>
/// <param name="FailedServices">The number of failed services (summary column).</param>
internal sealed record ServiceStatusFragment(int? TotalServices, int? FailedServices);
