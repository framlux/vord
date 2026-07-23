// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;

namespace Framlux.FleetManagement.Test.Models;

/// <summary>
/// Tests for <see cref="RetentionClassPolicy"/>, the pure mapping from a tenant's effective
/// retention days to the physical <see cref="RetentionClass"/> its telemetry is stamped with, and
/// the fixed drop-window constants each class owns.
/// </summary>
public sealed class RetentionClassPolicyTests
{
    // The owner-approved class-assignment matrix: the smallest class whose window physically covers
    // the tenant's effective retention. With the standard tiers this degenerates to Free -> Short,
    // Pro -> Medium, Team -> Long, but an override tenant is placed by its effective days, not tier.
    [Test]
    [Arguments(1, RetentionClass.Short)]
    [Arguments(30, RetentionClass.Medium)]
    [Arguments(60, RetentionClass.Medium)]
    [Arguments(61, RetentionClass.Long)]
    [Arguments(365, RetentionClass.Long)]
    [Arguments(400, RetentionClass.Long)]
    public async Task Classify_MapsEffectiveRetentionDaysToSmallestCoveringClass(int retentionDays, RetentionClass expected)
    {
        RetentionClass result = RetentionClassPolicy.Classify(retentionDays);

        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-5)]
    public async Task Classify_UnknownOrNonPositive_FallsBackToShortCheapestClass(int retentionDays)
    {
        // A missing or nonsensical retention value must fail safe to the cheapest (Short) class so a
        // resolution glitch can never route a row into a longer-lived partition than the plan allows.
        RetentionClass result = RetentionClassPolicy.Classify(retentionDays);

        await Assert.That(result).IsEqualTo(RetentionClass.Short);
    }
}
