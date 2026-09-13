// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Machines;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>Tests for the single derivation of machine liveness used by every read path.</summary>
public class MachineLivenessTests
{
    [Test]
    [Arguments((short)0, true)]
    [Arguments((short)1, true)]
    [Arguments((short)2, true)]
    [Arguments((short)3, false)]
    public async Task IsOnline_FollowsTheHealthStatus(short healthStatus, bool expected)
    {
        await Assert.That(MachineLiveness.IsOnline(healthStatus)).IsEqualTo(expected);
    }

    [Test]
    public async Task IsOnline_AbsentSummary_IsOffline()
    {
        // The fleet query maps a missing summary row to Offline; every other read path must agree,
        // or a machine reads differently depending on which screen you opened.
        await Assert.That(MachineLiveness.IsOnline((MachineStateSummary?)null)).IsFalse();
    }

    [Test]
    public async Task IsOnline_PresentSummary_FollowsItsHealthStatus()
    {
        MachineStateSummary online = new() { MachineId = 1, TenantId = 1, HealthStatus = 1 };
        MachineStateSummary offline = new() { MachineId = 2, TenantId = 1, HealthStatus = 3 };

        await Assert.That(MachineLiveness.IsOnline(online)).IsTrue();
        await Assert.That(MachineLiveness.IsOnline(offline)).IsFalse();
    }

    [Test]
    public async Task LastSeen_TakesTheMoreRecentChannel()
    {
        DateTimeOffset older = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset newer = older.AddMinutes(3);

        await Assert.That(MachineLiveness.LastSeen(older, newer)).IsEqualTo(newer);
        await Assert.That(MachineLiveness.LastSeen(newer, older)).IsEqualTo(newer);
    }

    [Test]
    public async Task LastSeen_ToleratesEitherChannelBeingAbsent()
    {
        DateTimeOffset seen = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

        await Assert.That(MachineLiveness.LastSeen(seen, null)).IsEqualTo(seen);
        await Assert.That(MachineLiveness.LastSeen(null, seen)).IsEqualTo(seen);
        await Assert.That(MachineLiveness.LastSeen(null, null)).IsNull();
    }
}
