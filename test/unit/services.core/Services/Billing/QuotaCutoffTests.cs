// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="QuotaCutoff"/>: one id describes the whole paused set, with the id itself
/// staying active because it is the last machine inside the limit.
/// </summary>
public sealed class QuotaCutoffTests
{
    [Test]
    public async Task None_PausesNoMachine()
    {
        await Assert.That(QuotaCutoff.None.IsPaused(1)).IsFalse();
        await Assert.That(QuotaCutoff.None.IsPaused(long.MaxValue)).IsFalse();
    }

    [Test]
    public async Task All_PausesEveryMachine()
    {
        await Assert.That(QuotaCutoff.All.IsPaused(1)).IsTrue();
        await Assert.That(QuotaCutoff.All.IsPaused(long.MaxValue)).IsTrue();
    }

    [Test]
    [Arguments(9L, false)]
    [Arguments(10L, false)]
    [Arguments(11L, true)]
    [Arguments(long.MaxValue, true)]
    public async Task After_PausesOnlyIdsGreaterThanTheCutoff(long machineId, bool expected)
    {
        await Assert.That(QuotaCutoff.After(10).IsPaused(machineId)).IsEqualTo(expected);
    }

    [Test]
    public async Task Factories_ProduceTheExpectedKinds()
    {
        await Assert.That(QuotaCutoff.None.Kind).IsEqualTo(QuotaCutoffKind.None);
        await Assert.That(QuotaCutoff.All.Kind).IsEqualTo(QuotaCutoffKind.All);
        await Assert.That(QuotaCutoff.After(7)).IsEqualTo(new QuotaCutoff(QuotaCutoffKind.After, 7));
    }
}
