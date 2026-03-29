// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the various types of machines.
/// </summary>
public enum MachineTypes : byte
{
    /// <summary>
    /// Represents an unknown machine type.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Represents a desktop machine.
    /// </summary>
    Desktop = 1,

    /// <summary>
    /// Represents a laptop machine.
    /// </summary>
    Laptop = 2,

    /// <summary>
    /// Represents a bare metal server.
    /// </summary>
    BareMetalServer = 3,

    /// <summary>
    /// Represents a virtual machine.
    /// </summary>
    VirtualMachine = 4
}
