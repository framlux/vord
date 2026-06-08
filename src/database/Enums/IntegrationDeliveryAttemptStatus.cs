// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Lifecycle status of an integration delivery attempt. The row is inserted with
/// <see cref="Pending"/> BEFORE the outbound HTTP POST so a worker crash or Hangfire retry
/// between send and record cannot cause a duplicate delivery. The row transitions to
/// <see cref="Succeeded"/> only after a 2xx response. Permanent (4xx) failures leave the row
/// in <see cref="Pending"/> to suppress retries; transient failures delete the row so a retry
/// can re-claim.
/// </summary>
public enum IntegrationDeliveryAttemptStatus : int
{
    /// <summary>Claim has been recorded; HTTP delivery is in-flight or terminally failed (4xx).</summary>
    Pending = 0,
    /// <summary>HTTP delivery returned 2xx — receiver has the notification.</summary>
    Succeeded = 1
}
