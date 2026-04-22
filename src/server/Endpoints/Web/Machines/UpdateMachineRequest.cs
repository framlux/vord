// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Request model for updating a machine's editable metadata.
/// </summary>
public sealed class UpdateMachineRequest
{
    /// <summary>
    /// The machine's display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// An optional description of the machine.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The machine's physical location.
    /// </summary>
    public string? Location { get; set; }
}
