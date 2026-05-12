// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Billing;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="BillingStatus"/>.
/// </summary>
public sealed class BillingStatusTests
{
    [Test]
    public async Task IsEnabled_WhenBillingEnabledNotSet_DefaultsToFalse()
    {
        IOptions<BillingOptions> options = Options.Create(new BillingOptions());

        BillingStatus billingStatus = new(options);

        await Assert.That(billingStatus.IsEnabled).IsFalse();
    }
}
