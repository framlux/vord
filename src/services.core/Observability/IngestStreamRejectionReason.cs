// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Why a telemetry stream was refused, or closed before the agent chose to close it. A refused
/// stream carries no envelope, so none of this is visible in envelope outcomes — and a fleet-wide
/// refusal at stream open looks exactly like a fleet that stopped sending.
/// </summary>
public enum IngestStreamRejectionReason
{
    /// <summary>
    /// The caller's machine identity could not be established, or contradicted its API key.
    /// </summary>
    Identity,

    /// <summary>
    /// The tenant's subscription is not eligible for ingest, either at open or on a mid-stream
    /// recheck.
    /// </summary>
    NotEntitled,

    /// <summary>
    /// The machine already holds the maximum number of concurrent streams.
    /// </summary>
    StreamLimit,
}
