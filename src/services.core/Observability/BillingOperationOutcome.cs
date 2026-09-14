// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// How a control-plane call ended.
/// </summary>
public enum BillingOperationOutcome
{
    /// <summary>The call reached billing-api and it did what was asked.</summary>
    Ok,

    /// <summary>The call reached billing-api and it declined. These return a failure flag rather
    /// than throwing, so transport instrumentation records them as successful.</summary>
    Failed,

    /// <summary>The call did not complete — a deadline, a transport failure, or an unreachable
    /// service. Nothing can be concluded about what billing-api would have said.</summary>
    Error,
}
