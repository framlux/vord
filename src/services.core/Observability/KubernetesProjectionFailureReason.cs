// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Why the Kubernetes inventory projection skipped a telemetry row instead of applying it.
/// </summary>
public enum KubernetesProjectionFailureReason
{
    /// <summary>The stored payload could not be read as a node report or described no usable node.</summary>
    InvalidPayload,

    /// <summary>Applying the row kept failing with errors that look temporary, until the retry budget ran out.</summary>
    RetriesExhausted,

    /// <summary>Applying the row failed with an error that retrying cannot fix.</summary>
    ApplyFailed,
}
