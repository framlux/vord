// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// The job queues this system runs. Mapped from the name Hangfire storage reports, because a queue
/// name read back from storage is a runtime string and a mistyped queue attribute would otherwise
/// mint a permanent series.
/// </summary>
/// <remarks>
/// The member names are the tag vocabulary, not the queue names Hangfire uses: the queue Hangfire
/// calls <c>long</c> is reported as <c>long_running</c>, because a member named <c>Long</c> collides
/// with a type name. The mapping lives in one place so the two cannot drift.
/// </remarks>
public enum HangfireQueueKind
{
    /// <summary>Per-minute jobs that must not be starved.</summary>
    Critical,

    /// <summary>Admin and UI-initiated work.</summary>
    Default,

    /// <summary>Multi-minute jobs that would otherwise hog every worker. Hangfire calls this queue
    /// <c>long</c>; the tag value is <c>long_running</c>.</summary>
    LongRunning,

    /// <summary>A queue name this build does not know about.</summary>
    Other,
}
