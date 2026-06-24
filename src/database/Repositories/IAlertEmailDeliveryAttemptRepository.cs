// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for the alert email delivery idempotency table. The two-state design
/// (Pending / Succeeded) — combined with the unique index on (AlertEventId, Recipient) —
/// guarantees at-most-once email delivery across Hangfire retries even if a worker crashes
/// between the outbound send and the success record:
/// <list type="number">
///   <item>Before sending, the worker calls <see cref="TryClaimAttemptAsync"/> to insert a
///         Pending row. A concurrent claim returns false and the worker skips this
///         recipient.</item>
///   <item>On a successful send the worker calls <see cref="MarkAttemptSucceededAsync"/> to
///         transition the row to Succeeded.</item>
///   <item>On a transient failure the worker calls <see cref="ReleaseClaimForRetryAsync"/> to
///         delete the Pending row so a Hangfire retry can re-claim. Permanent failures
///         intentionally leave the Pending row in place so retries are suppressed.</item>
/// </list>
/// </summary>
public interface IAlertEmailDeliveryAttemptRepository
{
    /// <summary>
    /// Returns the set of recipient email addresses that have ANY row (Pending or Succeeded) for
    /// the given alert event. Used by the delivery pre-check: a claimed row — regardless of
    /// status — means "do not re-attempt." The set is case-insensitive.
    /// </summary>
    /// <param name="alertEventId">The alert event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The set of recipients already claimed for this event.</returns>
    Task<HashSet<string>> GetClaimedRecipientsAsync(long alertEventId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a Pending claim row for the given (event, recipient). Returns true on successful
    /// insert (caller owns the delivery), false if a row already exists (concurrent claim or
    /// prior attempt — caller must skip).
    /// </summary>
    /// <param name="alertEventId">The alert event id.</param>
    /// <param name="recipient">The recipient email address.</param>
    /// <param name="attemptedAt">UTC timestamp of the claim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the caller now owns the delivery; <c>false</c> if the row already exists.</returns>
    Task<bool> TryClaimAttemptAsync(long alertEventId, string recipient, DateTimeOffset attemptedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Transitions a Pending claim to Succeeded after a successful send. No-op if the row is
    /// already Succeeded or does not exist.
    /// </summary>
    /// <param name="alertEventId">The alert event id.</param>
    /// <param name="recipient">The recipient email address.</param>
    /// <param name="succeededAt">UTC timestamp of the success.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MarkAttemptSucceededAsync(long alertEventId, string recipient, DateTimeOffset succeededAt, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a Pending claim row so a Hangfire retry can re-claim. Used only for transient
    /// failures. No-op if the row is Succeeded — succeeded deliveries must NEVER be retried
    /// because the recipient already has the email.
    /// </summary>
    /// <param name="alertEventId">The alert event id.</param>
    /// <param name="recipient">The recipient email address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReleaseClaimForRetryAsync(long alertEventId, string recipient, CancellationToken cancellationToken);
}
