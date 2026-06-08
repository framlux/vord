// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IIntegrationDeliveryAttemptRepository
{
    /// <inheritdoc/>
    public async Task<HashSet<int>> GetClaimedIntegrationIdsAsync(long alertEventId, CancellationToken cancellationToken)
    {
        // Any row — Pending or Succeeded — counts as "claimed". The delivery pre-check uses this
        // to skip integrations that already have a claim from a prior attempt; permanent (4xx)
        // failures intentionally leave the Pending row in place to suppress retries.
        List<int> ids = await _db.IntegrationDeliveryAttempts
            .Where(a => a.AlertEventId == alertEventId)
            .Select(a => a.IntegrationEndpointId)
            .ToListAsync(cancellationToken);

        return new HashSet<int>(ids);
    }

    /// <inheritdoc/>
    public async Task<bool> TryClaimAttemptAsync(long alertEventId, int integrationEndpointId, DateTimeOffset attemptedAt, CancellationToken cancellationToken)
    {
        // The unique index UX_IntegrationDeliveryAttempts_EventIntegration guarantees at most
        // one claim row per (event, integration). Concurrent inserts race: the loser sees a
        // unique-violation and reports "already claimed" so the caller skips the integration.
        try
        {
            await _db.InsertAsync(
                new IntegrationDeliveryAttempt
                {
                    AlertEventId = alertEventId,
                    IntegrationEndpointId = integrationEndpointId,
                    Status = IntegrationDeliveryAttemptStatus.Pending,
                    AttemptedAt = attemptedAt,
                    SucceededAt = null,
                },
                token: cancellationToken);

            return true;
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task MarkAttemptSucceededAsync(long alertEventId, int integrationEndpointId, DateTimeOffset succeededAt, CancellationToken cancellationToken)
    {
        // Only Pending rows transition to Succeeded. The status guard makes a redundant call
        // (e.g., a Hangfire replay after MarkAttemptSucceeded already ran) a no-op so the
        // succeeded timestamp is not silently overwritten by a later retry.
        await _db.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == alertEventId)
                        && (a.IntegrationEndpointId == integrationEndpointId)
                        && (a.Status == IntegrationDeliveryAttemptStatus.Pending))
            .Set(a => a.Status, IntegrationDeliveryAttemptStatus.Succeeded)
            .Set(a => a.SucceededAt, (DateTimeOffset?)succeededAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ReleaseClaimForRetryAsync(long alertEventId, int integrationEndpointId, CancellationToken cancellationToken)
    {
        // Only delete Pending rows. Succeeded rows MUST be preserved — the receiver has the
        // notification and a Hangfire retry must not re-POST it. The status filter is the
        // safety belt that makes "release on transient failure" correct even when a concurrent
        // worker has already marked success.
        await _db.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == alertEventId)
                        && (a.IntegrationEndpointId == integrationEndpointId)
                        && (a.Status == IntegrationDeliveryAttemptStatus.Pending))
            .DeleteAsync(cancellationToken);
    }
}
