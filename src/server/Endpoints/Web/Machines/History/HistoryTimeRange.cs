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
