// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.Test.Services;

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
        Framlux.FleetManagement.Services.Core.Options.TierLimitDefaults defaults = new()
        {
            MachineLimit = 3,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            MemberLimit = 1,
        };

        await Assert.That(defaults.MemberLimit).IsEqualTo(1);
    }
}
