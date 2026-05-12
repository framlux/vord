// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Telemetry;

/// <summary>
/// Redis-backed deduplication service for telemetry event IDs.
/// </summary>
public interface ITelemetryDeduplicationService
{
    /// <summary>
    /// Atomically checks if the event ID has been seen before and marks it as seen.
    /// Returns true if the event ID is new (not seen within the TTL window).
    /// </summary>
    /// <param name="eventId">The telemetry event ID to check.</param>
    /// <returns>True if the event ID is new; false if it was already seen.</returns>
    Task<bool> TryMarkSeenAsync(string eventId);

    /// <summary>
    /// Checks and marks multiple event IDs in a single pipelined Redis round-trip.
    /// Returns a dictionary mapping each event ID to whether it is new.
    /// </summary>
    /// <param name="eventIds">The telemetry event IDs to check.</param>
    /// <returns>A dictionary mapping each event ID to true if new, false if duplicate.</returns>
    Task<Dictionary<string, bool>> TryMarkSeenBatchAsync(IEnumerable<string> eventIds);
}
