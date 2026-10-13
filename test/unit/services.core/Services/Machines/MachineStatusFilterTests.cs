// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Models.Machines;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>
/// Tests for <see cref="MachineStatusFilter"/>, the one place the machine list and the dashboard
/// decide what a status name means.
/// </summary>
public class MachineStatusFilterTests
{
    private static MachineStateSummary SummaryWith(MachineHealthStatus health)
    {
        return new MachineStateSummary { MachineId = 1, TenantId = 1, HealthStatus = (short)health };
    }

    [Test]
    [Arguments("healthy", MachineHealthStatus.Healthy)]
    [Arguments("warning", MachineHealthStatus.Warning)]
    [Arguments("critical", MachineHealthStatus.Critical)]
    [Arguments("offline", MachineHealthStatus.Offline)]
    public async Task Parse_HealthName_SelectsExactlyThatHealth(string name, MachineHealthStatus expected)
    {
        MachineStatusFilter? filter = MachineStatusFilter.Parse(name);

        await Assert.That(filter).IsNotNull();
        await Assert.That(filter!.HealthStatuses).IsEquivalentTo(new List<MachineHealthStatus> { expected });
        await Assert.That(filter.PausedOnly).IsFalse();
    }

    [Test]
    public async Task Parse_Online_SelectsEveryHealthButOffline()
    {
        MachineStatusFilter? filter = MachineStatusFilter.Parse("online");

        await Assert.That(filter).IsNotNull();
        await Assert.That(filter!.HealthStatuses).IsEquivalentTo(new List<MachineHealthStatus>
        {
            MachineHealthStatus.Healthy,
            MachineHealthStatus.Warning,
            MachineHealthStatus.Critical,
        });
        await Assert.That(filter.PausedOnly).IsFalse();
    }

    [Test]
    public async Task Parse_Paused_SelectsNoHealthAndOnlyPaused()
    {
        MachineStatusFilter? filter = MachineStatusFilter.Parse("paused");

        await Assert.That(filter).IsNotNull();
        await Assert.That(filter!.HealthStatuses.Count).IsEqualTo(0);
        await Assert.That(filter.PausedOnly).IsTrue();
    }

    [Test]
    [Arguments("CRITICAL")]
    [Arguments("Critical")]
    [Arguments("cRiTiCaL")]
    public async Task Parse_IgnoresCase(string name)
    {
        MachineStatusFilter? filter = MachineStatusFilter.Parse(name);

        await Assert.That(filter).IsNotNull();
        await Assert.That(filter!.HealthStatuses).IsEquivalentTo(new List<MachineHealthStatus> { MachineHealthStatus.Critical });
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("all")]
    [Arguments("degraded")]
    [Arguments("healthy,warning")]
    public async Task Parse_BlankOrUnrecognisedName_AppliesNoFilter(string? name)
    {
        await Assert.That(MachineStatusFilter.Parse(name)).IsNull();
    }

    [Test]
    [Arguments("healthy", MachineHealthStatus.Healthy, true)]
    [Arguments("healthy", MachineHealthStatus.Warning, false)]
    [Arguments("warning", MachineHealthStatus.Warning, true)]
    [Arguments("warning", MachineHealthStatus.Critical, false)]
    [Arguments("critical", MachineHealthStatus.Critical, true)]
    [Arguments("critical", MachineHealthStatus.Offline, false)]
    [Arguments("offline", MachineHealthStatus.Offline, true)]
    [Arguments("offline", MachineHealthStatus.Healthy, false)]
    [Arguments("online", MachineHealthStatus.Healthy, true)]
    [Arguments("online", MachineHealthStatus.Warning, true)]
    [Arguments("online", MachineHealthStatus.Critical, true)]
    [Arguments("online", MachineHealthStatus.Offline, false)]
    [Arguments("paused", MachineHealthStatus.Healthy, false)]
    [Arguments("paused", MachineHealthStatus.Critical, false)]
    public async Task Matches_UnpausedMachine_FollowsItsSweptHealth(string name, MachineHealthStatus health, bool expected)
    {
        MachineStatusFilter filter = MachineStatusFilter.Parse(name)!;

        await Assert.That(filter.Matches(SummaryWith(health), isPaused: false)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("healthy")]
    [Arguments("warning")]
    [Arguments("critical")]
    [Arguments("offline")]
    [Arguments("online")]
    public async Task Matches_PausedMachine_NeverMatchesAHealthFilterWhateverItsFrozenHealth(string name)
    {
        // A paused machine's stored health is frozen at its last report, so it is outside every
        // health bucket. Checked against all four frozen values so none can slip through.
        MachineStatusFilter filter = MachineStatusFilter.Parse(name)!;

        foreach (MachineHealthStatus frozen in Enum.GetValues<MachineHealthStatus>())
        {
            await Assert.That(filter.Matches(SummaryWith(frozen), isPaused: true)).IsFalse();
        }
    }

    [Test]
    public async Task Matches_PausedFilter_MatchesOnlyPausedMachinesWhateverTheirHealth()
    {
        MachineStatusFilter filter = MachineStatusFilter.Parse("paused")!;

        await Assert.That(filter.Matches(SummaryWith(MachineHealthStatus.Critical), isPaused: true)).IsTrue();
        await Assert.That(filter.Matches(null, isPaused: true)).IsTrue();
        await Assert.That(filter.Matches(SummaryWith(MachineHealthStatus.Critical), isPaused: false)).IsFalse();
    }

    [Test]
    public async Task Matches_MachineWithNoSummaryRow_ReadsAsOffline()
    {
        await Assert.That(MachineStatusFilter.Parse("offline")!.Matches(null, isPaused: false)).IsTrue();
        await Assert.That(MachineStatusFilter.Parse("online")!.Matches(null, isPaused: false)).IsFalse();
        await Assert.That(MachineStatusFilter.Parse("healthy")!.Matches(null, isPaused: false)).IsFalse();
    }
}
