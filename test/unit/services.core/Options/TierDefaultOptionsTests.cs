// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.Options;

namespace Framlux.FleetManagement.Test.Validators;

/// <summary>
/// Tests for <see cref="TierDefaultOptions.For"/>. Every limit fold resolves a tier's defaults
/// through it, so a wrong mapping would hand one tier another tier's limits.
/// </summary>
public sealed class TierDefaultOptionsTests
{
    private static TierDefaultOptions Distinct()
    {
        return new TierDefaultOptions
        {
            Free = new TierLimitDefaults { MachineLimit = 3 },
            Pro = new TierLimitDefaults { MachineLimit = 1000 },
            Team = new TierLimitDefaults { MachineLimit = 10000 },
            Enterprise = new TierLimitDefaults { MachineLimit = 20000 },
        };
    }

    /// <summary>
    /// Each real tier resolves to its own property.
    /// </summary>
    [Test]
    public async Task For_EachTier_ReturnsThatTiersDefaults()
    {
        TierDefaultOptions options = Distinct();

        await Assert.That(options.For(SubscriptionTier.Free)).IsSameReferenceAs(options.Free);
        await Assert.That(options.For(SubscriptionTier.Pro)).IsSameReferenceAs(options.Pro);
        await Assert.That(options.For(SubscriptionTier.Team)).IsSameReferenceAs(options.Team);
        await Assert.That(options.For(SubscriptionTier.Enterprise)).IsSameReferenceAs(options.Enterprise);
    }

    /// <summary>
    /// An unrecognised tier resolves to Free, the same way a missing subscription does, so an
    /// unknown value can never widen a limit.
    /// </summary>
    [Test]
    public async Task For_UnknownTier_ReturnsFree()
    {
        TierDefaultOptions options = Distinct();

        await Assert.That(options.For((SubscriptionTier)99)).IsSameReferenceAs(options.Free);
    }

    /// <summary>
    /// The unsubscribed tier value carries no limits of its own, so it falls to Free as well.
    /// </summary>
    [Test]
    public async Task For_NoneTier_ReturnsFree()
    {
        TierDefaultOptions options = Distinct();

        await Assert.That(options.For(SubscriptionTier.None)).IsSameReferenceAs(options.Free);
    }
}
