// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IAlertEmailDeliveryAttemptRepository
{
    /// <inheritdoc/>
    public async Task<HashSet<string>> GetClaimedRecipientsAsync(long alertEventId, CancellationToken cancellationToken)
    {
        // Any row — Pending or Succeeded — counts as "claimed". The delivery pre-check uses this
        // to skip recipients that already have a claim from a prior attempt; permanent failures
        // intentionally leave the Pending row in place to suppress retries.
        List<string> recipients = await _db.AlertEmailDeliveryAttempts
            .Where(a => a.AlertEventId == alertEventId)
            .Select(a => a.Recipient)
            .ToListAsync(cancellationToken);

        return new HashSet<string>(recipients, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async Task<bool> TryClaimAttemptAsync(long alertEventId, string recipient, DateTimeOffset attemptedAt, CancellationToken cancellationToken)
    {
        // The unique index UX_AlertEmailDeliveryAttempts_EventRecipient guarantees at most one
        // claim row per (event, recipient). Concurrent inserts race: the loser sees a
        // unique-violation and reports "already claimed" so the caller skips the recipient.
        try
        {
            await _db.InsertAsync(
                new AlertEmailDeliveryAttempt
                {
                    AlertEventId = alertEventId,
                    Recipient = recipient,
                    Status = EmailDeliveryAttemptStatus.Pending,
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
    public async Task MarkAttemptSucceededAsync(long alertEventId, string recipient, DateTimeOffset succeededAt, CancellationToken cancellationToken)
    {
        // Only Pending rows transition to Succeeded. The status guard makes a redundant call
        // (e.g., a Hangfire replay after MarkAttemptSucceeded already ran) a no-op so the
        // succeeded timestamp is not silently overwritten by a later retry.
        await _db.AlertEmailDeliveryAttempts
            .Where(a => (a.AlertEventId == alertEventId)
                        && (a.Recipient == recipient)
                        && (a.Status == EmailDeliveryAttemptStatus.Pending))
            .Set(a => a.Status, EmailDeliveryAttemptStatus.Succeeded)
            .Set(a => a.SucceededAt, (DateTimeOffset?)succeededAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ReleaseClaimForRetryAsync(long alertEventId, string recipient, CancellationToken cancellationToken)
    {
        // Only delete Pending rows. Succeeded rows MUST be preserved — the recipient has the
        // email and a Hangfire retry must not re-send it. The status filter is the safety belt
        // that makes "release on transient failure" correct even when a concurrent worker has
        // already marked success.
        await _db.AlertEmailDeliveryAttempts
            .Where(a => (a.AlertEventId == alertEventId)
                        && (a.Recipient == recipient)
                        && (a.Status == EmailDeliveryAttemptStatus.Pending))
            .DeleteAsync(cancellationToken);
    }
}
