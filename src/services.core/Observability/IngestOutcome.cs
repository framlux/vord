// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// The terminal state of one telemetry envelope. Every agent envelope the server answers ends in
/// exactly one of these, so the four together are the whole of ingest volume.
/// </summary>
public enum IngestOutcome
{
    /// <summary>
    /// Acknowledged as successfully processed. Items already seen are skipped inside a successful
    /// envelope, so an envelope whose items were all duplicates is accepted rather than being an
    /// outcome of its own.
    /// </summary>
    Accepted,

    /// <summary>
    /// Refused because of a defect in what the agent sent, which a retry will not fix.
    /// </summary>
    Rejected,

    /// <summary>
    /// Not taken right now — the server asked the agent to retry later. This is the outcome a
    /// database outage or an open circuit breaker moves, and without it such an outage would leave
    /// the envelope counter completely unmoved.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Refused because the tenant's subscription is not eligible for ingest.
    /// </summary>
    NotEntitled,
}
