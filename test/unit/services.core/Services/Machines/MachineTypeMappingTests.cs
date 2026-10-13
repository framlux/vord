// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.Machines;
using WireMachineType = Framlux.FleetManagement.Grpc.AgentRegistration.MachineType;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>
/// Tests for <see cref="MachineTypeMapping"/>, the one place a machine type an agent sends becomes
/// the type the database stores.
/// </summary>
public sealed class MachineTypeMappingTests
{
    [Test]
    [Arguments(WireMachineType.UnknownType, MachineTypes.Unknown)]
    [Arguments(WireMachineType.DesktopType, MachineTypes.Desktop)]
    [Arguments(WireMachineType.LaptopType, MachineTypes.Laptop)]
    [Arguments(WireMachineType.BareMetalServerType, MachineTypes.BareMetalServer)]
    [Arguments(WireMachineType.VirtualMachineType, MachineTypes.VirtualMachine)]
    [Arguments(WireMachineType.SingleBoardComputerType, MachineTypes.SingleBoardComputer)]
    public async Task ToStored_EveryDefinedType_MapsToItsStoredValue(WireMachineType registered, MachineTypes expected)
    {
        await Assert.That(MachineTypeMapping.ToStored(registered)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(6)]
    [Arguments(999)]
    [Arguments(-1)]
    public async Task ToStored_UndefinedType_IsUnknown(int raw)
    {
        // Registration refuses an undefined value before it gets here; if one ever did, Unknown is
        // the one stored value that claims nothing.
        await Assert.That(MachineTypeMapping.ToStored((WireMachineType)raw)).IsEqualTo(MachineTypes.Unknown);
    }

    [Test]
    [Arguments(WireMachineType.DesktopType, MachineTypes.Desktop)]
    [Arguments(WireMachineType.LaptopType, MachineTypes.Laptop)]
    [Arguments(WireMachineType.BareMetalServerType, MachineTypes.BareMetalServer)]
    [Arguments(WireMachineType.VirtualMachineType, MachineTypes.VirtualMachine)]
    [Arguments(WireMachineType.SingleBoardComputerType, MachineTypes.SingleBoardComputer)]
    public async Task ToCorrection_EveryClassifiedType_MapsToItsStoredValue(WireMachineType reported, MachineTypes expected)
    {
        await Assert.That(MachineTypeMapping.ToCorrection(reported)).IsEqualTo(expected);
    }

    [Test]
    public async Task ToCorrection_UnknownType_IsNoOpinion()
    {
        // UnknownType means the agent could not classify the host, not that the host is unknown.
        // Treating it as an assertion would let a detection failure erase a good stored value.
        await Assert.That(MachineTypeMapping.ToCorrection(WireMachineType.UnknownType)).IsNull();
    }

    [Test]
    [Arguments(6)]
    [Arguments(999)]
    [Arguments(-1)]
    public async Task ToCorrection_UndefinedType_IsIgnored(int raw)
    {
        // An agent must not be able to write an enum value the rest of the system cannot read.
        await Assert.That(MachineTypeMapping.ToCorrection((WireMachineType)raw)).IsNull();
    }

    [Test]
    public async Task EveryDefinedWireType_IsMappedAndKeepsItsNumber()
    {
        // A value added to the contract but not to the mapping would be stored as Unknown, or
        // ignored as a correction, with no error anywhere.
        foreach (WireMachineType wire in Enum.GetValues<WireMachineType>())
        {
            MachineTypes stored = MachineTypeMapping.ToStored(wire);

            await Assert.That((int)stored).IsEqualTo((int)wire).Because($"{wire} must be stored under its wire number");
            await Assert.That(Enum.IsDefined(stored)).IsTrue().Because($"{wire} must map to a defined stored value");

            if (wire != WireMachineType.UnknownType)
            {
                await Assert.That(MachineTypeMapping.ToCorrection(wire)).IsEqualTo(stored).Because($"{wire} must be accepted as a correction");
            }
        }
    }
}
