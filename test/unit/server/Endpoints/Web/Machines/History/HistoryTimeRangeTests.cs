// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Machines.History;

/// <summary>
/// Unit tests for <see cref="HistoryTimeRange"/> covering all methods and branches,
/// including retention boundary conditions and error message content.
/// </summary>
public class HistoryTimeRangeTests
{

    // ================================================================
    // TryResolve tests
    // ================================================================

    [Test]
    public async Task TryResolve_ValidRange_WithinRetention_ReturnsOk()
    {
        // "1h" = 1/24 of a day; retention of 1 day is sufficient
        HistoryRangeResult result = HistoryTimeRange.TryResolve("1h", 1, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.Ok);
        await Assert.That(error).IsEqualTo(string.Empty);
        await Assert.That(rangeStart).IsLessThan(rangeEnd);
        await Assert.That((rangeEnd - rangeStart).TotalSeconds).IsEqualTo(3600.0);
    }

    [Test]
    public async Task TryResolve_7d_WithinRetention_Of30Days_ReturnsOk()
    {
        HistoryRangeResult result = HistoryTimeRange.TryResolve("7d", 30, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.Ok);
        await Assert.That(error).IsEqualTo(string.Empty);
        await Assert.That((rangeEnd - rangeStart).TotalSeconds).IsEqualTo(604800.0);
    }

    [Test]
    public async Task TryResolve_30d_ExceedsRetentionOf7_ReturnsRetentionExceeded()
    {
        // "30d" = 30 days; tenant only has 7 days of retention
        HistoryRangeResult result = HistoryTimeRange.TryResolve("30d", 7, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.RetentionExceeded);
        await Assert.That(error).IsNotEmpty();
        // On error, out params should remain at now (rangeStart == rangeEnd)
        await Assert.That(rangeStart).IsEqualTo(rangeEnd);
    }

    [Test]
    public async Task TryResolve_RetentionExceeded_ErrorMessage_ContainsRetentionDays()
    {
        // The error message must tell the user how many days of retention they have
        HistoryRangeResult result = HistoryTimeRange.TryResolve("30d", 7, out DateTimeOffset _, out DateTimeOffset _, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.RetentionExceeded);
        await Assert.That(error).Contains("7");
    }

    [Test]
    public async Task TryResolve_RetentionExceeded_ErrorMessage_MentionsHigherTier()
    {
        // The error message should suggest upgrading to a higher tier
        HistoryRangeResult result = HistoryTimeRange.TryResolve("30d", 1, out DateTimeOffset _, out DateTimeOffset _, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.RetentionExceeded);
        await Assert.That(error).Contains("higher subscription tier");
    }

    [Test]
    public async Task TryResolve_ExactlyAtRetentionBoundary_30d_With30DayRetention_ReturnsOk()
    {
        // "30d" = exactly 30 days; retention is 30 days — boundary condition, should be allowed
        HistoryRangeResult result = HistoryTimeRange.TryResolve("30d", 30, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.Ok);
        await Assert.That(error).IsEqualTo(string.Empty);
        await Assert.That((rangeEnd - rangeStart).TotalSeconds).IsEqualTo(2592000.0);
    }

    [Test]
    public async Task TryResolve_OneDayOverRetentionBoundary_30d_With29DayRetention_ReturnsRetentionExceeded()
    {
        // "30d" = 30 days; retention is 29 — one day over the limit
        HistoryRangeResult result = HistoryTimeRange.TryResolve("30d", 29, out DateTimeOffset _, out DateTimeOffset _, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.RetentionExceeded);
        await Assert.That(error).Contains("29");
    }

    [Test]
    public async Task TryResolve_7d_ExactlyAtRetentionBoundary_ReturnsOk()
    {
        // "7d" = 7 days; retention is 7 — exactly at boundary
        HistoryRangeResult result = HistoryTimeRange.TryResolve("7d", 7, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.Ok);
        await Assert.That(error).IsEqualTo(string.Empty);
        await Assert.That((rangeEnd - rangeStart).TotalSeconds).IsEqualTo(604800.0);
    }

    [Test]
    public async Task TryResolve_7d_OneDayOverBoundary_ReturnsRetentionExceeded()
    {
        // "7d" = 7 days; retention is 6 — one day over
        HistoryRangeResult result = HistoryTimeRange.TryResolve("7d", 6, out DateTimeOffset _, out DateTimeOffset _, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.RetentionExceeded);
        await Assert.That(error).Contains("6");
    }

    [Test]
    public async Task TryResolve_NullRange_ReturnsInvalidRange()
    {
        HistoryRangeResult result = HistoryTimeRange.TryResolve(null, 30, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.InvalidRange);
        await Assert.That(error).IsNotEmpty();
        await Assert.That(rangeStart).IsEqualTo(rangeEnd);
    }

    [Test]
    public async Task TryResolve_EmptyRange_ReturnsInvalidRange()
    {
        HistoryRangeResult result = HistoryTimeRange.TryResolve(string.Empty, 30, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.InvalidRange);
        await Assert.That(error).IsNotEmpty();
        await Assert.That(rangeStart).IsEqualTo(rangeEnd);
    }

    [Test]
    public async Task TryResolve_WhitespaceRange_ReturnsInvalidRange()
    {
        HistoryRangeResult result = HistoryTimeRange.TryResolve("   ", 30, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.InvalidRange);
        await Assert.That(error).IsNotEmpty();
        await Assert.That(rangeStart).IsEqualTo(rangeEnd);
    }

    [Test]
    public async Task TryResolve_GarbageRange_ReturnsInvalidRange()
    {
        HistoryRangeResult result = HistoryTimeRange.TryResolve("99x", 30, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.InvalidRange);
        await Assert.That(error).IsNotEmpty();
        await Assert.That(rangeStart).IsEqualTo(rangeEnd);
    }

    [Test]
    public async Task TryResolve_InvalidRange_ErrorMessage_ListsValidValues()
    {
        // The error message should enumerate the valid options so users know what to provide
        HistoryRangeResult result = HistoryTimeRange.TryResolve("bogus", 30, out DateTimeOffset _, out DateTimeOffset _, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.InvalidRange);
        await Assert.That(error).Contains("1h");
        await Assert.That(error).Contains("7d");
        await Assert.That(error).Contains("30d");
    }

    [Test]
    public async Task TryResolve_ValidRange_OutParamsReflectCorrectWindow()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        HistoryRangeResult result = HistoryTimeRange.TryResolve("24h", 30, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd, out string error);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        await Assert.That(result).IsEqualTo(HistoryRangeResult.Ok);
        await Assert.That(error).IsEqualTo(string.Empty);
        await Assert.That(rangeEnd).IsGreaterThanOrEqualTo(before);
        await Assert.That(rangeEnd).IsLessThanOrEqualTo(after);
        await Assert.That(rangeStart).IsLessThan(rangeEnd);
        await Assert.That((rangeEnd - rangeStart).TotalSeconds).IsEqualTo(86400.0);
    }

    [Test]
    public async Task TryResolve_1h_SubHourRetention_StillAllowed_BecauseRetentionIsMeasuredInDays()
    {
        // Retention is 1 day, and 1h = 1/24 day < 1 day; must succeed
        HistoryRangeResult result = HistoryTimeRange.TryResolve("1h", 1, out DateTimeOffset _, out DateTimeOffset _, out string error);

        await Assert.That(result).IsEqualTo(HistoryRangeResult.Ok);
        await Assert.That(error).IsEqualTo(string.Empty);
    }
}
