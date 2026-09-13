// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints;
using Framlux.FleetManagement.Services.Core.Models;

namespace Framlux.FleetManagement.Test.Endpoints;

/// <summary>
/// Unit tests for the shared pageSize query rule.
/// </summary>
public sealed class PageSizeQueryTests
{
    [Test]
    public async Task TryResolve_Omitted_IsServedTheDefault()
    {
        bool ok = PageSizeQuery.TryResolve(null, out int pageSize);

        await Assert.That(ok).IsTrue();
        await Assert.That(pageSize).IsEqualTo(PaginationLimits.DefaultPageSize);
    }

    [Test]
    public async Task TryResolve_WithinRange_IsHonouredExactly()
    {
        bool ok = PageSizeQuery.TryResolve(50, out int pageSize);

        await Assert.That(ok).IsTrue();
        await Assert.That(pageSize).IsEqualTo(50);
    }

    [Test]
    public async Task TryResolve_AtTheCeiling_IsHonoured()
    {
        bool ok = PageSizeQuery.TryResolve(PaginationLimits.MaxPageSize, out int pageSize);

        await Assert.That(ok).IsTrue();
        await Assert.That(pageSize).IsEqualTo(PaginationLimits.MaxPageSize);
    }

    [Test]
    public async Task TryResolve_AboveTheCeiling_IsRefusedRatherThanReduced()
    {
        bool ok = PageSizeQuery.TryResolve(PaginationLimits.MaxPageSize + 1, out int pageSize);

        // The out value is deliberately not the ceiling: a caller that ignored the false return
        // and used it anyway would serve a clamped page, which is the behaviour being removed.
        await Assert.That(ok).IsFalse();
        await Assert.That(pageSize).IsNotEqualTo(PaginationLimits.MaxPageSize);
    }

    [Test]
    public async Task TryResolve_Zero_IsRefused()
    {
        await Assert.That(PageSizeQuery.TryResolve(0, out _)).IsFalse();
    }

    [Test]
    public async Task TryResolve_Negative_IsRefused()
    {
        await Assert.That(PageSizeQuery.TryResolve(-1, out _)).IsFalse();
    }

    [Test]
    public async Task OutOfRangeMessage_NamesTheCeiling()
    {
        await Assert.That(PageSizeQuery.OutOfRangeMessage).Contains(PaginationLimits.MaxPageSize.ToString());
    }
}
