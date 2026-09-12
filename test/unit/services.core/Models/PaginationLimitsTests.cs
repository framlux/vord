// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models;

namespace Framlux.FleetManagement.UnitTest.Services.Core.Models;

/// <summary>
/// Unit tests for the shared pagination bounds.
/// </summary>
public sealed class PaginationLimitsTests
{
    [Test]
    public async Task IsValidPageSize_AtTheLowerBound_IsAccepted()
    {
        await Assert.That(PaginationLimits.IsValidPageSize(1)).IsTrue();
    }

    [Test]
    public async Task IsValidPageSize_AtTheCeiling_IsAccepted()
    {
        await Assert.That(PaginationLimits.IsValidPageSize(PaginationLimits.MaxPageSize)).IsTrue();
    }

    [Test]
    public async Task IsValidPageSize_OneAboveTheCeiling_IsRejected()
    {
        await Assert.That(PaginationLimits.IsValidPageSize(PaginationLimits.MaxPageSize + 1)).IsFalse();
    }

    [Test]
    public async Task IsValidPageSize_Zero_IsRejected()
    {
        await Assert.That(PaginationLimits.IsValidPageSize(0)).IsFalse();
    }

    [Test]
    public async Task IsValidPageSize_Negative_IsRejected()
    {
        await Assert.That(PaginationLimits.IsValidPageSize(-1)).IsFalse();
    }

    [Test]
    public async Task DefaultPageSize_IsServable()
    {
        // A default the endpoints would themselves refuse would be an unreachable configuration.
        await Assert.That(PaginationLimits.IsValidPageSize(PaginationLimits.DefaultPageSize)).IsTrue();
    }
}
