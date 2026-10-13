// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.Machines;
using WireMachineType = Framlux.FleetManagement.Grpc.AgentRegistration.MachineType;

namespace Framlux.FleetManagement.UnitTest.Endpoints.Grpc;

/// <summary>
/// The machine type an agent sends and the machine type the database stores are two enums that
/// must agree number for number, because the stored byte is also what the web, the exports and the
/// filters read. A type added to one and not the other is stored as Unknown or refused.
/// </summary>
public sealed class MachineTypeParityTests
{
    [Test]
    public async Task EveryWireType_HasAStoredTypeWithTheSameNumber()
    {
        foreach (WireMachineType wire in Enum.GetValues<WireMachineType>())
        {
            MachineTypes stored = (MachineTypes)(byte)wire;

            await Assert.That(Enum.IsDefined(stored)).IsTrue().Because($"wire type {wire} has no stored value");
            await Assert.That(MachineTypeMapping.ToStored(wire)).IsEqualTo(stored);
        }
    }

    [Test]
    public async Task EveryStoredType_HasAWireTypeWithTheSameNumber()
    {
        foreach (MachineTypes stored in Enum.GetValues<MachineTypes>())
        {
            WireMachineType wire = (WireMachineType)(int)stored;

            await Assert.That(Enum.IsDefined(wire)).IsTrue().Because($"stored type {stored} has no wire value");
            await Assert.That(MachineTypeMapping.ToStored(wire)).IsEqualTo(stored);
        }
    }

    [Test]
    [Arguments(MachineTypes.Unknown, 0)]
    [Arguments(MachineTypes.Desktop, 1)]
    [Arguments(MachineTypes.Laptop, 2)]
    [Arguments(MachineTypes.BareMetalServer, 3)]
    [Arguments(MachineTypes.VirtualMachine, 4)]
    [Arguments(MachineTypes.SingleBoardComputer, 5)]
    public async Task StoredTypeNumbers_ArePinned(MachineTypes stored, int number)
    {
        // The number is what lives in the database, in exports and in the web's own enum.
        await Assert.That((int)stored).IsEqualTo(number);
    }

    [Test]
    [Arguments(WireMachineType.UnknownType, 0)]
    [Arguments(WireMachineType.DesktopType, 1)]
    [Arguments(WireMachineType.LaptopType, 2)]
    [Arguments(WireMachineType.BareMetalServerType, 3)]
    [Arguments(WireMachineType.VirtualMachineType, 4)]
    [Arguments(WireMachineType.SingleBoardComputerType, 5)]
    public async Task WireTypeNumbers_ArePinned(WireMachineType wire, int number)
    {
        // A renumbering would change what every deployed agent means by the value it sends.
        await Assert.That((int)wire).IsEqualTo(number);
    }

    [Test]
    public async Task StoredTypes_AreExactlyTheSixKnownValues()
    {
        // Exact membership rather than a count, so replacing one value with another still fails.
        await Assert.That(Enum.GetValues<MachineTypes>().Select(t => t.ToString()).Order().ToArray())
            .IsEquivalentTo(new[] { "BareMetalServer", "Desktop", "Laptop", "SingleBoardComputer", "Unknown", "VirtualMachine" });
    }
}
