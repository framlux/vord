// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Records the claim and outcome of an integration delivery for a single (eventId, integrationId)
/// pair. The row is inserted with <see cref="IntegrationDeliveryAttemptStatus.Pending"/> BEFORE
/// the outbound HTTP POST so a worker crash or Hangfire retry between send and record cannot
/// cause a duplicate delivery. On 2xx the row transitions to
/// <see cref="IntegrationDeliveryAttemptStatus.Succeeded"/>; on a permanent (4xx) failure the
/// row stays Pending to suppress retries; on a transient failure the row is deleted so a retry
/// can re-claim.
/// </summary>
[Table(TableNames.IntegrationDeliveryAttempts)]
public sealed class IntegrationDeliveryAttempt
{
    /// <summary>Primary key.</summary>
    [PrimaryKey, Identity]
    [Column("Id")]
    public long Id { get; set; }

    /// <summary>The alert event that was delivered.</summary>
    [Column("AlertEventId"), NotNull]
    public long AlertEventId { get; set; }

    /// <summary>The integration endpoint that received the delivery.</summary>
    [Column("IntegrationEndpointId"), NotNull]
    public int IntegrationEndpointId { get; set; }

    /// <summary>Lifecycle status (Pending or Succeeded).</summary>
    [Column("Status"), NotNull]
    public IntegrationDeliveryAttemptStatus Status { get; set; }

    /// <summary>UTC timestamp the claim was inserted (HTTP POST about to be attempted).</summary>
    [Column("AttemptedAt"), NotNull]
    public DateTimeOffset AttemptedAt { get; set; }

    /// <summary>UTC timestamp the delivery succeeded; null while the row is Pending.</summary>
    [Column("SucceededAt"), Nullable]
    public DateTimeOffset? SucceededAt { get; set; }
}
