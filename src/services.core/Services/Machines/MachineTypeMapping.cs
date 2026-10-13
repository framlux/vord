// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using WireMachineType = Framlux.FleetManagement.Grpc.AgentRegistration.MachineType;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// The one mapping from the machine type an agent sends to the type the database stores. A type
/// missing from it would otherwise be stored as Unknown, or silently ignored as a correction, with no
/// error anywhere, so registration and the configuration fetch both go through it.
/// </summary>
public static class MachineTypeMapping
{
    /// <summary>
    /// Maps the type an agent registers with onto the stored enum. A value the contract does not
    /// define is stored as Unknown, although registration refuses such a value before it gets here.
    /// </summary>
    /// <param name="registered">The machine type as sent by the agent.</param>
    /// <returns>The database enum value to store.</returns>
    public static MachineTypes ToStored(WireMachineType registered)
    {
        return Map(registered) ?? MachineTypes.Unknown;
    }

    /// <summary>
    /// Maps a machine type reported on a configuration fetch onto the stored enum, or null when it
    /// should be ignored.
    /// <para>
    /// UnknownType means the agent could not classify the host, which is not the same as asserting
    /// the host is unknown — treating it as an assertion would let a detection failure erase a good
    /// stored value. An undefined value is rejected for the same reason a registration would reject
    /// it: an agent must not be able to write an enum the rest of the system cannot read.
    /// </para>
    /// </summary>
    /// <param name="reported">The machine type as sent by the agent.</param>
    /// <returns>The database enum value to store, or null to leave the stored type alone.</returns>
    public static MachineTypes? ToCorrection(WireMachineType reported)
    {
        if (reported == WireMachineType.UnknownType)
        {
            return null;
        }

        return Map(reported);
    }

    private static MachineTypes? Map(WireMachineType machineType)
    {
        return machineType switch
        {
            WireMachineType.UnknownType => MachineTypes.Unknown,
            WireMachineType.DesktopType => MachineTypes.Desktop,
            WireMachineType.LaptopType => MachineTypes.Laptop,
            WireMachineType.BareMetalServerType => MachineTypes.BareMetalServer,
            WireMachineType.VirtualMachineType => MachineTypes.VirtualMachine,
            WireMachineType.SingleBoardComputerType => MachineTypes.SingleBoardComputer,
            _ => null,
        };
    }
}
