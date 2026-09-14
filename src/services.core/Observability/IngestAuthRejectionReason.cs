// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Why an agent request was refused before it reached any service. These rejections happen in the
/// API key authentication handler, so they are invisible to any instrument recorded inside the
/// telemetry service — and a fleet-wide key problem is one of the likeliest silent ingest failures.
/// </summary>
public enum IngestAuthRejectionReason
{
    /// <summary>
    /// No API key was presented.
    /// </summary>
    MissingKey,

    /// <summary>
    /// The key presented resolved to no machine. A revoked key is indistinguishable from one that
    /// never existed at this layer — the lookup answers null for both — so revocation is counted
    /// here rather than given a bucket the code could never fill.
    /// </summary>
    UnknownKey,
}
