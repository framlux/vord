// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Machines;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>
/// Tests for <see cref="AgentCapabilities"/>: the capability bits an agent reports about itself.
/// </summary>
public class AgentCapabilitiesTests
{
    [Test]
    public async Task RemoteCommands_IsBitZero()
    {
        // Intent: the value is part of the wire contract with the agent, so it is pinned exactly.
        ulong bit = AgentCapabilities.RemoteCommands;

        await Assert.That(bit).IsEqualTo(1UL);
    }

    [Test]
    [Arguments(0UL, false)]
    [Arguments(1UL, true)]
    [Arguments(2UL, false)]
    [Arguments(3UL, true)]
    [Arguments(ulong.MaxValue, true)]
    [Arguments(ulong.MaxValue - 1, false)]
    public async Task HasRemoteCommands_ChecksOnlyBitZero(ulong capabilities, bool expected)
    {
        await Assert.That(AgentCapabilities.HasRemoteCommands(capabilities)).IsEqualTo(expected);
    }
}
