// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Lifecycle status of an alert email delivery attempt. A claim row is inserted as
/// <see cref="Pending"/> before the outbound send and transitions to <see cref="Succeeded"/>
/// after a successful send; transient failures delete the Pending row so a Hangfire retry can
/// re-claim, while permanent failures leave it Pending to suppress retries.
/// </summary>
public enum EmailDeliveryAttemptStatus : int
{
    /// <summary>The send was claimed but has not yet succeeded.</summary>
    Pending = 0,
    /// <summary>The send completed successfully.</summary>
    Succeeded = 1
}
