// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Records the claim and outcome of an alert email delivery for a single (eventId, recipient)
/// pair. The row is inserted with <see cref="EmailDeliveryAttemptStatus.Pending"/> before the
/// outbound send so a worker crash or Hangfire retry between send and record cannot cause a
/// duplicate email. On success the row transitions to <see cref="EmailDeliveryAttemptStatus.Succeeded"/>;
/// on a permanent failure the row stays Pending to suppress retries; on a transient failure the
/// row is deleted so a retry can re-claim.
/// </summary>
[Table(TableNames.AlertEmailDeliveryAttempts)]
public sealed class AlertEmailDeliveryAttempt
{
    /// <summary>Primary key.</summary>
    [PrimaryKey, Identity]
    [Column("Id")]
    public long Id { get; set; }

    /// <summary>The alert event that was delivered.</summary>
    [Column("AlertEventId"), NotNull]
    public long AlertEventId { get; set; }

    /// <summary>The recipient email address that received the delivery.</summary>
    [Column("Recipient"), NotNull]
    public required string Recipient { get; set; }

    /// <summary>Lifecycle status (Pending or Succeeded).</summary>
    [Column("Status"), NotNull]
    public EmailDeliveryAttemptStatus Status { get; set; }

    /// <summary>UTC timestamp the claim was inserted (send about to be attempted).</summary>
    [Column("AttemptedAt"), NotNull]
    public DateTimeOffset AttemptedAt { get; set; }

    /// <summary>UTC timestamp the delivery succeeded; null while the row is Pending.</summary>
    [Column("SucceededAt"), Nullable]
    public DateTimeOffset? SucceededAt { get; set; }
}
