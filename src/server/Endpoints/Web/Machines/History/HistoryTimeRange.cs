// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;

/// <summary>
/// Resolves and validates time range parameters for history endpoints.
/// </summary>
public static class HistoryTimeRange
{
    private static readonly Dictionary<string, int> RangeToSeconds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1h"] = 3600,
        ["6h"] = 21600,
        ["24h"] = 86400,
        ["7d"] = 604800,
        ["30d"] = 2592000
    };

    /// <summary>
    /// Attempts to parse a range string into a time window.
    /// </summary>
    /// <param name="range">The range string (e.g., "1h", "24h", "7d").</param>
    /// <param name="rangeStart">The computed start of the time window.</param>
    /// <param name="rangeEnd">The computed end of the time window (now).</param>
    /// <returns>True if the range string is valid.</returns>
    public static bool TryParse(string? range, out DateTimeOffset rangeStart, out DateTimeOffset rangeEnd)
    {
        rangeEnd = DateTimeOffset.UtcNow;
        rangeStart = rangeEnd;

        if (string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        if (RangeToSeconds.TryGetValue(range, out int seconds) == false)
        {
            return false;
        }

        rangeStart = rangeEnd.AddSeconds(-seconds);

        return true;
    }

    /// <summary>
    /// Returns the number of days represented by a range string.
    /// </summary>
    /// <param name="range">The range string (e.g., "7d").</param>
    /// <returns>The number of days, or 0 if invalid.</returns>
    public static double GetRangeDays(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return 0;
        }

        if (RangeToSeconds.TryGetValue(range, out int seconds) == false)
        {
            return 0;
        }

        return seconds / 86400.0;
    }

    /// <summary>
    /// Checks whether the requested range is valid.
    /// </summary>
    /// <param name="range">The range string.</param>
    /// <returns>True if the range is a recognized value.</returns>
    public static bool IsValid(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        return RangeToSeconds.ContainsKey(range);
    }

    /// <summary>
    /// Validates a range string, checks it against a retention limit, and resolves
    /// the time window — all in a single dictionary lookup with one consistent UtcNow.
    /// </summary>
    /// <param name="range">The range string (e.g., "1h", "24h", "7d").</param>
    /// <param name="retentionDays">The tenant's retention limit in days.</param>
    /// <param name="rangeStart">The computed start of the time window.</param>
    /// <param name="rangeEnd">The computed end of the time window (now).</param>
    /// <param name="error">Describes the validation failure, if any.</param>
    /// <returns>A <see cref="HistoryRangeResult"/> indicating the outcome.</returns>
    public static HistoryRangeResult TryResolve(
        string? range,
        int retentionDays,
        out DateTimeOffset rangeStart,
        out DateTimeOffset rangeEnd,
        out string error)
    {
        rangeEnd = DateTimeOffset.UtcNow;
        rangeStart = rangeEnd;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(range) ||
            (RangeToSeconds.TryGetValue(range, out int seconds) == false))
        {
            error = "Invalid range parameter. Valid values: 1h, 6h, 24h, 7d, 30d";

            return HistoryRangeResult.InvalidRange;
        }

        double requestedDays = seconds / 86400.0;
        if (requestedDays > retentionDays)
        {
            error = $"This time range requires a higher subscription tier. Your current plan retains {retentionDays} day(s) of history.";

            return HistoryRangeResult.RetentionExceeded;
        }

        rangeStart = rangeEnd.AddSeconds(-seconds);

        return HistoryRangeResult.Ok;
    }
}

