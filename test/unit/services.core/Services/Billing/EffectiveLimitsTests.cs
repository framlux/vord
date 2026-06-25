// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Options;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="EffectiveLimits"/>.
/// </summary>
public sealed class EffectiveLimitsTests
{
    [Test]
    public async Task EffectiveLimits_CarriesMemberLimit()
    {
        EffectiveLimits limits = new()
        {
            MachineLimit = 3,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            MemberLimit = 1,
        };

        await Assert.That(limits.MemberLimit).IsEqualTo(1);
    }

    [Test]
    public async Task TierLimitDefaults_ExposesMemberLimit()
    {
        TierLimitDefaults defaults = new()
        {
            MachineLimit = 3,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            MemberLimit = 1,
        };

        await Assert.That(defaults.MemberLimit).IsEqualTo(1);
    }

    [Test]
    public async Task EffectiveLimits_UnlimitedSentinel_RoundTrips()
    {
        EffectiveLimits limits = new() { MemberLimit = int.MaxValue };

        await Assert.That(limits.MemberLimit).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task EffectiveLimits_DefaultMemberLimit_IsZero()
    {
        EffectiveLimits limits = new()
        {
            MachineLimit = 3,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
        };

        await Assert.That(limits.MemberLimit).IsEqualTo(0);
    }

    [Test]
    public async Task TierLimitDefaults_UnlimitedSentinel_RoundTrips()
    {
        TierLimitDefaults defaults = new() { MemberLimit = int.MaxValue };

        await Assert.That(defaults.MemberLimit).IsEqualTo(int.MaxValue);
    }
}
