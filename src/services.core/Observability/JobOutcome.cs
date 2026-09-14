// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// How one background job run ended.
/// </summary>
public enum JobOutcome
{
    /// <summary>The job completed without throwing.</summary>
    Succeeded,

    /// <summary>The job threw. Hangfire may retry it; each attempt records separately.</summary>
    Failed,
}
