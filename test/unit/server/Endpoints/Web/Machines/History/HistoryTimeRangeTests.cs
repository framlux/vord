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
    // TryParse tests
    // ================================================================

    [Test]
    public async Task TryParse_ValidRange_1h_ReturnsTrue()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        bool result = HistoryTimeRange.TryParse("1h", out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        await Assert.That(result).IsTrue();
        await Assert.That(rangeEnd).IsGreaterThanOrEqualTo(before);
        await Assert.That(rangeEnd).IsLessThanOrEqualTo(after);
        await Assert.That((rangeEnd - rangeStart).TotalSeconds).IsEqualTo(3600.0);
    }

    [Test]
    public async Task TryParse_ValidRange_6h_ReturnsTrue()
    {
        bool result = HistoryTimeRange.TryParse("6h", out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd);

        await Assert.That(result).IsTrue();
        await Assert.That((rangeEnd - rangeStart).TotalSeconds).IsEqualTo(21600.0);
    }

    [Test]
    public async Task TryParse_ValidRange_24h_ReturnsTrue()
    {
        bool result = HistoryTimeRange.TryParse("24h", out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd);

        await Assert.That(result).IsTrue();
        await Assert.That((rangeEnd - rangeStart).TotalSeconds).IsEqualTo(86400.0);
    }

    [Test]
    public async Task TryParse_ValidRange_7d_ReturnsTrue()
    {
        bool result = HistoryTimeRange.TryParse("7d", out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd);

        await Assert.That(result).IsTrue();
        await Assert.That((rangeEnd - rangeStart).TotalSeconds).IsEqualTo(604800.0);
    }

    [Test]
    public async Task TryParse_ValidRange_30d_ReturnsTrue()
    {
        bool result = HistoryTimeRange.TryParse("30d", out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd);

        await Assert.That(result).IsTrue();
        await Assert.That((rangeEnd - rangeStart).TotalSeconds).IsEqualTo(2592000.0);
    }

    [Test]
    public async Task TryParse_NullRange_ReturnsFalse()
    {
        bool result = HistoryTimeRange.TryParse(null, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd);

        await Assert.That(result).IsFalse();
        await Assert.That(rangeStart).IsEqualTo(rangeEnd);
    }

    [Test]
    public async Task TryParse_EmptyRange_ReturnsFalse()
    {
        bool result = HistoryTimeRange.TryParse(string.Empty, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd);

        await Assert.That(result).IsFalse();
        await Assert.That(rangeStart).IsEqualTo(rangeEnd);
    }

    [Test]
    public async Task TryParse_WhitespaceRange_ReturnsFalse()
    {
        bool result = HistoryTimeRange.TryParse("   ", out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd);

        await Assert.That(result).IsFalse();
        await Assert.That(rangeStart).IsEqualTo(rangeEnd);
    }

    [Test]
    public async Task TryParse_UnrecognizedRange_99x_ReturnsFalse()
    {
        bool result = HistoryTimeRange.TryParse("99x", out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd);

        await Assert.That(result).IsFalse();
        await Assert.That(rangeStart).IsEqualTo(rangeEnd);
    }

    [Test]
    public async Task TryParse_CaseInsensitive_UppercaseKey_ReturnsTrue()
    {
        // The dictionary uses StringComparer.OrdinalIgnoreCase, so "1H" must match "1h"
        bool result = HistoryTimeRange.TryParse("1H", out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd);

        await Assert.That(result).IsTrue();
        await Assert.That((rangeEnd - rangeStart).TotalSeconds).IsEqualTo(3600.0);
    }

    [Test]
    public async Task TryParse_CaseInsensitive_MixedCase_7D_ReturnsTrue()
    {
        bool result = HistoryTimeRange.TryParse("7D", out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd);

        await Assert.That(result).IsTrue();
        await Assert.That((rangeEnd - rangeStart).TotalSeconds).IsEqualTo(604800.0);
    }

    [Test]
    public async Task TryParse_RangeStartIsBeforeRangeEnd()
    {
        bool result = HistoryTimeRange.TryParse("24h", out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd);

        await Assert.That(result).IsTrue();
        await Assert.That(rangeStart).IsLessThan(rangeEnd);
    }

    // ================================================================
    // GetRangeDays tests
    // ================================================================

    [Test]
    public async Task GetRangeDays_1h_ReturnsCorrectFractionalDay()
    {
        // 3600 / 86400 = 0.041666...
        double result = HistoryTimeRange.GetRangeDays("1h");

        await Assert.That(result).IsEqualTo(3600.0 / 86400.0);
    }

    [Test]
    public async Task GetRangeDays_6h_ReturnsCorrectFractionalDay()
    {
        double result = HistoryTimeRange.GetRangeDays("6h");

        await Assert.That(result).IsEqualTo(21600.0 / 86400.0);
    }

    [Test]
    public async Task GetRangeDays_24h_ReturnsOne()
    {
        double result = HistoryTimeRange.GetRangeDays("24h");

        await Assert.That(result).IsEqualTo(1.0);
    }

    [Test]
    public async Task GetRangeDays_7d_ReturnsSeven()
    {
        double result = HistoryTimeRange.GetRangeDays("7d");

        await Assert.That(result).IsEqualTo(7.0);
    }

    [Test]
    public async Task GetRangeDays_30d_ReturnsThirty()
    {
        double result = HistoryTimeRange.GetRangeDays("30d");

        await Assert.That(result).IsEqualTo(30.0);
    }

    [Test]
    public async Task GetRangeDays_NullRange_ReturnsZero()
    {
        double result = HistoryTimeRange.GetRangeDays(null);

        await Assert.That(result).IsEqualTo(0.0);
    }

    [Test]
    public async Task GetRangeDays_EmptyRange_ReturnsZero()
    {
        double result = HistoryTimeRange.GetRangeDays(string.Empty);

        await Assert.That(result).IsEqualTo(0.0);
    }

    [Test]
    public async Task GetRangeDays_WhitespaceRange_ReturnsZero()
    {
        double result = HistoryTimeRange.GetRangeDays("   ");

        await Assert.That(result).IsEqualTo(0.0);
    }

    [Test]
    public async Task GetRangeDays_UnrecognizedRange_ReturnsZero()
    {
        double result = HistoryTimeRange.GetRangeDays("90d");

        await Assert.That(result).IsEqualTo(0.0);
    }

    // ================================================================
    // IsValid tests
    // ================================================================

    [Test]
    public async Task IsValid_1h_ReturnsTrue()
    {
        bool result = HistoryTimeRange.IsValid("1h");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsValid_6h_ReturnsTrue()
    {
        bool result = HistoryTimeRange.IsValid("6h");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsValid_24h_ReturnsTrue()
    {
        bool result = HistoryTimeRange.IsValid("24h");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsValid_7d_ReturnsTrue()
    {
        bool result = HistoryTimeRange.IsValid("7d");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsValid_30d_ReturnsTrue()
    {
        bool result = HistoryTimeRange.IsValid("30d");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsValid_UnrecognizedRange_90d_ReturnsFalse()
    {
        bool result = HistoryTimeRange.IsValid("90d");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsValid_NullRange_ReturnsFalse()
    {
        bool result = HistoryTimeRange.IsValid(null);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsValid_EmptyRange_ReturnsFalse()
    {
        bool result = HistoryTimeRange.IsValid(string.Empty);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsValid_WhitespaceRange_ReturnsFalse()
    {
        bool result = HistoryTimeRange.IsValid("   ");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsValid_GarbageRange_ReturnsFalse()
    {
        bool result = HistoryTimeRange.IsValid("xyz");

        await Assert.That(result).IsFalse();
    }

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
