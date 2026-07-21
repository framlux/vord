// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints.Web.Billing;
using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Billing;

/// <summary>
/// Tests for <see cref="BillingIntervalFormat"/>.
/// </summary>
public sealed class BillingIntervalFormatTests
{
    [Test]
    public async Task ToWireString_Monthly_ReturnsMonthly()
    {
        await Assert.That(BillingIntervalFormat.ToWireString(BillingInterval.Monthly)).IsEqualTo("monthly");
    }

    [Test]
    public async Task ToWireString_Annual_ReturnsAnnual()
    {
        await Assert.That(BillingIntervalFormat.ToWireString(BillingInterval.Annual)).IsEqualTo("annual");
    }

    [Test]
    public async Task ToWireString_None_ReturnsNull()
    {
        await Assert.That(BillingIntervalFormat.ToWireString(BillingInterval.None)).IsNull();
    }

    [Test]
    public async Task ToWireString_UndefinedEnumValue_ReturnsNull()
    {
        await Assert.That(BillingIntervalFormat.ToWireString((BillingInterval)99)).IsNull();
    }
}
