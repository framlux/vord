// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for the IntegrationDeliveryJob idempotency table. The two-state design
/// (Pending / Succeeded) — combined with the unique index on (AlertEventId,
/// IntegrationEndpointId) — guarantees at-most-once delivery across Hangfire retries even if a
/// worker crashes between the outbound HTTP POST and the success record:
/// <list type="number">
///   <item>Before sending, the worker calls <see cref="TryClaimAttemptAsync"/> to insert a
///         Pending row. A concurrent claim returns false and the worker skips this
///         integration.</item>
///   <item>On 2xx the worker calls <see cref="MarkAttemptSucceededAsync"/> to transition the
///         row to Succeeded.</item>
///   <item>On a transient failure (5xx / transport error) the worker calls
///         <see cref="ReleaseClaimForRetryAsync"/> to delete the Pending row so a Hangfire
///         retry can re-claim. Permanent (4xx) failures intentionally leave the Pending row in
///         place so retries are suppressed.</item>
/// </list>
/// </summary>
public interface IIntegrationDeliveryAttemptRepository
{
    /// <summary>
    /// Returns the set of integration endpoint ids that have ANY row (Pending or Succeeded) for
    /// the given alert event. Used by the delivery pre-check: a claimed row — regardless of
    /// status — means "do not re-attempt."
    /// </summary>
    /// <param name="alertEventId">The alert event id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The set of integration ids already claimed for this event.</returns>
    Task<HashSet<int>> GetClaimedIntegrationIdsAsync(long alertEventId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a Pending claim row for the given (event, integration). Returns true on
    /// successful insert (caller owns the delivery), false if a row already exists (concurrent
    /// claim or prior attempt — caller must skip).
    /// </summary>
    /// <param name="alertEventId">The alert event id.</param>
    /// <param name="integrationEndpointId">The integration endpoint id.</param>
    /// <param name="attemptedAt">UTC timestamp of the claim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the caller now owns the delivery; <c>false</c> if the row already exists.</returns>
    Task<bool> TryClaimAttemptAsync(long alertEventId, int integrationEndpointId, DateTimeOffset attemptedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Transitions a Pending claim to Succeeded after a 2xx delivery. No-op if the row is
    /// already Succeeded or does not exist.
    /// </summary>
    /// <param name="alertEventId">The alert event id.</param>
    /// <param name="integrationEndpointId">The integration endpoint id.</param>
    /// <param name="succeededAt">UTC timestamp of the success.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MarkAttemptSucceededAsync(long alertEventId, int integrationEndpointId, DateTimeOffset succeededAt, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a Pending claim row so a Hangfire retry can re-claim. Used only for transient
    /// failures (5xx / transport errors). No-op if the row is Succeeded — succeeded deliveries
    /// must NEVER be retried because the receiver already has the notification.
    /// </summary>
    /// <param name="alertEventId">The alert event id.</param>
    /// <param name="integrationEndpointId">The integration endpoint id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReleaseClaimForRetryAsync(long alertEventId, int integrationEndpointId, CancellationToken cancellationToken);
}
