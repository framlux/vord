// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Repositories;

public sealed class AlertEmailDeliveryAttemptRepositoryTests
{
    private static IAlertEmailDeliveryAttemptRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }

    [Test]
    public async Task TryClaimAttemptAsync_FirstCall_ReturnsTrueAndInsertsPendingRow()
    {
        // Intent: the first claim for a (event, recipient) pair must insert a Pending row with
        // the AttemptedAt timestamp and no SucceededAt. The pre-send claim is the entire point of
        // the two-state design — a crash between send and success record cannot duplicate.
        using TestDatabaseFactory dbFactory = new();
        IAlertEmailDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset attemptedAt = new(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
        bool claimed = await repo.TryClaimAttemptAsync(alertEventId: 42, recipient: "alice@example.com", attemptedAt, CancellationToken.None);

        await Assert.That(claimed).IsTrue();

        AlertEmailDeliveryAttempt? row = await dbFactory.Context.AlertEmailDeliveryAttempts
            .Where(a => (a.AlertEventId == 42) && (a.Recipient == "alice@example.com"))
            .FirstOrDefaultAsync();

        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Status).IsEqualTo(EmailDeliveryAttemptStatus.Pending);
        await Assert.That(row.AttemptedAt).IsEqualTo(attemptedAt);
        await Assert.That(row.SucceededAt).IsNull();
    }

    [Test]
    public async Task TryClaimAttemptAsync_RowAlreadyExists_ReturnsFalse()
    {
        // Intent: a second claim attempt against the same (event, recipient) must return false so
        // the caller skips the send. The unique index UX_AlertEmailDeliveryAttempts_EventRecipient
        // is what makes the design idempotent across Hangfire retries.
        using TestDatabaseFactory dbFactory = new();
        IAlertEmailDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool first = await repo.TryClaimAttemptAsync(alertEventId: 1, recipient: "ops@example.com", now, CancellationToken.None);
        bool second = await repo.TryClaimAttemptAsync(alertEventId: 1, recipient: "ops@example.com", now.AddSeconds(1), CancellationToken.None);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();

        int count = await dbFactory.Context.AlertEmailDeliveryAttempts
            .Where(a => (a.AlertEventId == 1) && (a.Recipient == "ops@example.com"))
            .CountAsync();
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task MarkAttemptSucceededAsync_PendingRow_TransitionsToSucceeded()
    {
        // Intent: a successful send must flip the existing Pending claim to Succeeded, stamping
        // the success timestamp. The original AttemptedAt must survive.
        using TestDatabaseFactory dbFactory = new();
        IAlertEmailDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset attemptedAt = new(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
        await repo.TryClaimAttemptAsync(alertEventId: 9, recipient: "bob@example.com", attemptedAt, CancellationToken.None);

        DateTimeOffset succeededAt = attemptedAt.AddSeconds(2);
        await repo.MarkAttemptSucceededAsync(alertEventId: 9, recipient: "bob@example.com", succeededAt, CancellationToken.None);

        AlertEmailDeliveryAttempt row = await dbFactory.Context.AlertEmailDeliveryAttempts
            .Where(a => (a.AlertEventId == 9) && (a.Recipient == "bob@example.com"))
            .FirstAsync();

        await Assert.That(row.Status).IsEqualTo(EmailDeliveryAttemptStatus.Succeeded);
        await Assert.That(row.SucceededAt).IsEqualTo(succeededAt);
        await Assert.That(row.AttemptedAt).IsEqualTo(attemptedAt);
    }

    [Test]
    public async Task MarkAttemptSucceededAsync_AlreadySucceeded_IsNoOp()
    {
        // Intent: replaying MarkAttemptSucceeded after a row already transitioned must not
        // overwrite the original SucceededAt. The Status=Pending guard makes the operation
        // idempotent against Hangfire replay.
        using TestDatabaseFactory dbFactory = new();
        IAlertEmailDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset attemptedAt = new(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
        await repo.TryClaimAttemptAsync(11, "carol@example.com", attemptedAt, CancellationToken.None);

        DateTimeOffset firstSuccess = attemptedAt.AddSeconds(1);
        await repo.MarkAttemptSucceededAsync(11, "carol@example.com", firstSuccess, CancellationToken.None);

        DateTimeOffset secondSuccess = attemptedAt.AddSeconds(99);
        await repo.MarkAttemptSucceededAsync(11, "carol@example.com", secondSuccess, CancellationToken.None);

        AlertEmailDeliveryAttempt row = await dbFactory.Context.AlertEmailDeliveryAttempts
            .Where(a => (a.AlertEventId == 11) && (a.Recipient == "carol@example.com"))
            .FirstAsync();

        await Assert.That(row.Status).IsEqualTo(EmailDeliveryAttemptStatus.Succeeded);
        await Assert.That(row.SucceededAt).IsEqualTo(firstSuccess);
    }

    [Test]
    public async Task ReleaseClaimForRetryAsync_PendingRow_DeletesRow()
    {
        // Intent: on a transient failure the caller releases the claim so a Hangfire retry can
        // re-claim. The Pending row must be deleted so a subsequent TryClaim returns true.
        using TestDatabaseFactory dbFactory = new();
        IAlertEmailDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await repo.TryClaimAttemptAsync(77, "dan@example.com", now, CancellationToken.None);

        await repo.ReleaseClaimForRetryAsync(77, "dan@example.com", CancellationToken.None);

        int count = await dbFactory.Context.AlertEmailDeliveryAttempts
            .Where(a => (a.AlertEventId == 77) && (a.Recipient == "dan@example.com"))
            .CountAsync();
        await Assert.That(count).IsEqualTo(0);

        bool reclaimed = await repo.TryClaimAttemptAsync(77, "dan@example.com", now.AddSeconds(5), CancellationToken.None);
        await Assert.That(reclaimed).IsTrue();
    }

    [Test]
    public async Task ReleaseClaimForRetryAsync_SucceededRow_DoesNotDelete()
    {
        // Intent: Succeeded rows MUST be preserved. If a buggy code path or a concurrent worker
        // called ReleaseClaim against a row already marked Succeeded, the recipient would receive
        // a duplicate email on the next retry. The status guard prevents that.
        using TestDatabaseFactory dbFactory = new();
        IAlertEmailDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await repo.TryClaimAttemptAsync(33, "erin@example.com", now, CancellationToken.None);
        await repo.MarkAttemptSucceededAsync(33, "erin@example.com", now.AddSeconds(1), CancellationToken.None);

        await repo.ReleaseClaimForRetryAsync(33, "erin@example.com", CancellationToken.None);

        AlertEmailDeliveryAttempt? row = await dbFactory.Context.AlertEmailDeliveryAttempts
            .Where(a => (a.AlertEventId == 33) && (a.Recipient == "erin@example.com"))
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Status).IsEqualTo(EmailDeliveryAttemptStatus.Succeeded);
    }

    [Test]
    public async Task GetClaimedRecipientsAsync_NoAttempts_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEmailDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        HashSet<string> recipients = await repo.GetClaimedRecipientsAsync(alertEventId: 1, CancellationToken.None);

        await Assert.That(recipients.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TryClaimAttemptAsync_ConcurrentCalls_ExactlyOneReturnsTrue()
    {
        // Intent: concurrent claim attempts must produce exactly one winner. SQLite serializes
        // writes (the in-memory connection has a single writer at a time), but the test pins the
        // contract regardless of backend: the unique index makes "exactly one winner" a property
        // of the data, not the runtime. Under Postgres in production the concurrent INSERTs race
        // and SQLSTATE 23505 surfaces to the losers.
        using TestDatabaseFactory dbFactory = new();
        IAlertEmailDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Task<bool>[] claims =
        [
            repo.TryClaimAttemptAsync(50, "race@example.com", now, CancellationToken.None),
            repo.TryClaimAttemptAsync(50, "race@example.com", now, CancellationToken.None),
            repo.TryClaimAttemptAsync(50, "race@example.com", now, CancellationToken.None),
        ];

        bool[] results = await Task.WhenAll(claims);

        int winners = results.Count(r => r);
        int losers = results.Count(r => r == false);
        await Assert.That(winners).IsEqualTo(1);
        await Assert.That(losers).IsEqualTo(2);
    }

    [Test]
    public async Task TryClaimAttempt_SetsStatusToPending_NotRelyingOnDefault()
    {
        // Intent: the repository's TryClaimAttemptAsync path MUST set Status explicitly so the
        // column default is never relied on. This guards against an accidental future change that
        // omits Status from the insert — a row inserted without an explicit Status would otherwise
        // silently appear as already-delivered.
        using TestDatabaseFactory dbFactory = new();
        IAlertEmailDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        await repo.TryClaimAttemptAsync(alertEventId: 99, recipient: "status@example.com", DateTimeOffset.UtcNow, CancellationToken.None);

        AlertEmailDeliveryAttempt? attempt = await dbFactory.Context.AlertEmailDeliveryAttempts
            .Where(a => (a.AlertEventId == 99) && (a.Recipient == "status@example.com"))
            .FirstOrDefaultAsync();
        await Assert.That(attempt).IsNotNull();
        await Assert.That(attempt!.Status).IsEqualTo(EmailDeliveryAttemptStatus.Pending);
        await Assert.That(attempt.SucceededAt).IsNull();
    }

    [Test]
    public async Task GetClaimedRecipientsAsync_ReturnsBothPendingAndSucceeded_CaseInsensitive()
    {
        // Intent: the pre-check must treat ANY claim — Pending or Succeeded — as "do not
        // re-attempt." A permanent failure leaves the row Pending; the next retry must still skip
        // it. The set is case-insensitive so an email address differing only in case still hits.
        using TestDatabaseFactory dbFactory = new();
        IAlertEmailDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        // Pending — simulates a permanent failure that left the claim in place.
        await repo.TryClaimAttemptAsync(alertEventId: 100, recipient: "pending@example.com", now, CancellationToken.None);
        // Succeeded — simulates a happy-path delivery.
        await repo.TryClaimAttemptAsync(alertEventId: 100, recipient: "done@example.com", now, CancellationToken.None);
        await repo.MarkAttemptSucceededAsync(alertEventId: 100, recipient: "done@example.com", now.AddSeconds(1), CancellationToken.None);

        // Different event — must not appear in event 100's claimed set.
        await repo.TryClaimAttemptAsync(alertEventId: 200, recipient: "other@example.com", now, CancellationToken.None);

        HashSet<string> event100 = await repo.GetClaimedRecipientsAsync(100, CancellationToken.None);
        HashSet<string> event200 = await repo.GetClaimedRecipientsAsync(200, CancellationToken.None);

        await Assert.That(event100.Count).IsEqualTo(2);
        await Assert.That(event100.Contains("PENDING@example.com")).IsTrue();
        await Assert.That(event100.Contains("Done@Example.com")).IsTrue();
        await Assert.That(event200.Count).IsEqualTo(1);
        await Assert.That(event200.Contains("other@example.com")).IsTrue();
    }
}
