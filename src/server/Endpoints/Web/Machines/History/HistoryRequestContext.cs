// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;

/// <summary>
/// Validated context for a history endpoint request.
/// Contains the resolved time range and machine identity after all checks pass.
/// </summary>
public sealed class HistoryRequestContext
{
    /// <summary>The machine ID from the route.</summary>
    public required long MachineId { get; init; }

    /// <summary>The tenant ID from the authenticated user's claims.</summary>
    public required int TenantId { get; init; }

    /// <summary>Inclusive start of the resolved time range.</summary>
    public required DateTimeOffset RangeStart { get; init; }

    /// <summary>Exclusive end of the resolved time range (now).</summary>
    public required DateTimeOffset RangeEnd { get; init; }
}
