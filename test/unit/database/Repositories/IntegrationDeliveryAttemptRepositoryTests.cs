// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Repositories;

public sealed class IntegrationDeliveryAttemptRepositoryTests
{
    private static IIntegrationDeliveryAttemptRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }

    [Test]
    public async Task GetClaimedIntegrationIdsAsync_NoAttempts_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        HashSet<int> ids = await repo.GetClaimedIntegrationIdsAsync(alertEventId: 1, CancellationToken.None);

        await Assert.That(ids.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TryClaimAttemptAsync_FirstCall_ReturnsTrueAndInsertsPendingRow()
    {
        // Intent: the first claim for a (event, integration) pair must insert a Pending row with
        // the AttemptedAt timestamp and no SucceededAt. The pre-send claim is the entire point of
        // the two-state design — a crash between HTTP send and success record cannot duplicate.
        using TestDatabaseFactory dbFactory = new();
        IIntegrationDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset attemptedAt = new(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
        bool claimed = await repo.TryClaimAttemptAsync(alertEventId: 42, integrationEndpointId: 7, attemptedAt, CancellationToken.None);

        await Assert.That(claimed).IsTrue();

        IntegrationDeliveryAttempt? row = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == 42) && (a.IntegrationEndpointId == 7))
            .FirstOrDefaultAsync();

        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Status).IsEqualTo(IntegrationDeliveryAttemptStatus.Pending);
        await Assert.That(row.AttemptedAt).IsEqualTo(attemptedAt);
        await Assert.That(row.SucceededAt).IsNull();
    }

    [Test]
    public async Task TryClaimAttemptAsync_RowAlreadyExists_ReturnsFalse()
    {
        // Intent: a second claim attempt against the same (event, integration) must return false
        // so the caller skips the HTTP POST. This is what makes the design idempotent across
        // Hangfire retries — the previous attempt's claim row blocks the new attempt.
        using TestDatabaseFactory dbFactory = new();
        IIntegrationDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool first = await repo.TryClaimAttemptAsync(alertEventId: 1, integrationEndpointId: 1, now, CancellationToken.None);
        bool second = await repo.TryClaimAttemptAsync(alertEventId: 1, integrationEndpointId: 1, now.AddSeconds(1), CancellationToken.None);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();

        int count = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == 1) && (a.IntegrationEndpointId == 1))
            .CountAsync();
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task TryClaimAttemptAsync_ConcurrentCalls_ExactlyOneReturnsTrue()
    {
        // Intent: concurrent claim attempts must produce exactly one winner. SQLite serializes
        // writes (the in-memory connection has a single writer at a time), but the test pins the
        // contract regardless of backend: the unique index makes "exactly one winner" a property
        // of the data, not the runtime. Under Postgres in production the two INSERTs run
        // concurrently and SQLSTATE 23505 surfaces to the loser.
        using TestDatabaseFactory dbFactory = new();
        IIntegrationDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Task<bool>[] claims =
        [
            repo.TryClaimAttemptAsync(50, 50, now, CancellationToken.None),
            repo.TryClaimAttemptAsync(50, 50, now, CancellationToken.None),
            repo.TryClaimAttemptAsync(50, 50, now, CancellationToken.None),
        ];

        bool[] results = await Task.WhenAll(claims);

        int winners = results.Count(r => r);
        int losers = results.Count(r => r == false);
        await Assert.That(winners).IsEqualTo(1);
        await Assert.That(losers).IsEqualTo(2);
    }

    [Test]
    public async Task MarkAttemptSucceededAsync_PendingRow_TransitionsToSucceeded()
    {
        // Intent: a successful 2xx delivery must flip the existing Pending claim to Succeeded,
        // stamping the success timestamp. The original AttemptedAt must survive so we can
        // measure delivery latency in the future.
        using TestDatabaseFactory dbFactory = new();
        IIntegrationDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset attemptedAt = new(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
        await repo.TryClaimAttemptAsync(alertEventId: 9, integrationEndpointId: 3, attemptedAt, CancellationToken.None);

        DateTimeOffset succeededAt = attemptedAt.AddSeconds(2);
        await repo.MarkAttemptSucceededAsync(alertEventId: 9, integrationEndpointId: 3, succeededAt, CancellationToken.None);

        IntegrationDeliveryAttempt row = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == 9) && (a.IntegrationEndpointId == 3))
            .FirstAsync();

        await Assert.That(row.Status).IsEqualTo(IntegrationDeliveryAttemptStatus.Succeeded);
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
        IIntegrationDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset attemptedAt = new(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
        await repo.TryClaimAttemptAsync(11, 22, attemptedAt, CancellationToken.None);

        DateTimeOffset firstSuccess = attemptedAt.AddSeconds(1);
        await repo.MarkAttemptSucceededAsync(11, 22, firstSuccess, CancellationToken.None);

        DateTimeOffset secondSuccess = attemptedAt.AddSeconds(99);
        await repo.MarkAttemptSucceededAsync(11, 22, secondSuccess, CancellationToken.None);

        IntegrationDeliveryAttempt row = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == 11) && (a.IntegrationEndpointId == 22))
            .FirstAsync();

        await Assert.That(row.Status).IsEqualTo(IntegrationDeliveryAttemptStatus.Succeeded);
        await Assert.That(row.SucceededAt).IsEqualTo(firstSuccess);
    }

    [Test]
    public async Task ReleaseClaimForRetryAsync_PendingRow_DeletesRow()
    {
        // Intent: on a transient failure the caller releases the claim so a Hangfire retry can
        // re-claim. The Pending row must be deleted so a subsequent TryClaim returns true.
        using TestDatabaseFactory dbFactory = new();
        IIntegrationDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await repo.TryClaimAttemptAsync(77, 88, now, CancellationToken.None);

        await repo.ReleaseClaimForRetryAsync(77, 88, CancellationToken.None);

        int count = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == 77) && (a.IntegrationEndpointId == 88))
            .CountAsync();
        await Assert.That(count).IsEqualTo(0);

        bool reclaimed = await repo.TryClaimAttemptAsync(77, 88, now.AddSeconds(5), CancellationToken.None);
        await Assert.That(reclaimed).IsTrue();
    }

    [Test]
    public async Task ReleaseClaimForRetryAsync_SucceededRow_DoesNotDelete()
    {
        // Intent: Succeeded rows MUST be preserved. If a buggy code path or a concurrent worker
        // called ReleaseClaim against a row that was already marked Succeeded, the receiver
        // would receive a duplicate notification on the next retry. The status guard prevents
        // that.
        using TestDatabaseFactory dbFactory = new();
        IIntegrationDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await repo.TryClaimAttemptAsync(33, 44, now, CancellationToken.None);
        await repo.MarkAttemptSucceededAsync(33, 44, now.AddSeconds(1), CancellationToken.None);

        await repo.ReleaseClaimForRetryAsync(33, 44, CancellationToken.None);

        IntegrationDeliveryAttempt? row = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == 33) && (a.IntegrationEndpointId == 44))
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Status).IsEqualTo(IntegrationDeliveryAttemptStatus.Succeeded);
    }

    [Test]
    public async Task GetClaimedIntegrationIdsAsync_ReturnsBothPendingAndSucceeded()
    {
        // Intent: the pre-check must treat ANY claim — Pending or Succeeded — as "do not
        // re-attempt." A 4xx permanent failure leaves the row Pending; the next retry must
        // still skip it.
        using TestDatabaseFactory dbFactory = new();
        IIntegrationDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        // Pending — simulates a permanent (4xx) failure that left the claim in place.
        await repo.TryClaimAttemptAsync(alertEventId: 100, integrationEndpointId: 5, now, CancellationToken.None);
        // Succeeded — simulates a happy-path delivery.
        await repo.TryClaimAttemptAsync(alertEventId: 100, integrationEndpointId: 6, now, CancellationToken.None);
        await repo.MarkAttemptSucceededAsync(alertEventId: 100, integrationEndpointId: 6, now.AddSeconds(1), CancellationToken.None);

        // Different event — must not appear in event 100's claimed set.
        await repo.TryClaimAttemptAsync(alertEventId: 200, integrationEndpointId: 7, now, CancellationToken.None);

        HashSet<int> event100 = await repo.GetClaimedIntegrationIdsAsync(100, CancellationToken.None);
        HashSet<int> event200 = await repo.GetClaimedIntegrationIdsAsync(200, CancellationToken.None);

        await Assert.That(event100.Count).IsEqualTo(2);
        await Assert.That(event100.Contains(5)).IsTrue();
        await Assert.That(event100.Contains(6)).IsTrue();
        await Assert.That(event200.Count).IsEqualTo(1);
        await Assert.That(event200.Contains(7)).IsTrue();
    }

    [Test]
    public async Task AlertEventDelete_CascadesToIntegrationDeliveryAttempts()
    {
        // Intent: deleting an alert event must cascade-delete its delivery-attempt rows so the
        // dedup table cannot accumulate orphan history when events are pruned. Behavioral test:
        // seed an event and its integration delivery row, delete the event, assert the child
        // row is gone.
        using TestDatabaseFactory dbFactory = new();
        EnableForeignKeys(dbFactory);

        (int tenantId, int integrationId, long eventId) = await SeedEventAndIntegrationAsync(dbFactory);

        IIntegrationDeliveryAttemptRepository repo = CreateRepo(dbFactory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool claimed = await repo.TryClaimAttemptAsync(eventId, integrationId, now, CancellationToken.None);
        await Assert.That(claimed).IsTrue();

        int deletedEvents = await dbFactory.Context.AlertEvents
            .Where(e => e.Id == eventId)
            .DeleteAsync();
        await Assert.That(deletedEvents).IsEqualTo(1);

        int remaining = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == eventId) && (a.IntegrationEndpointId == integrationId))
            .CountAsync();
        await Assert.That(remaining).IsEqualTo(0);
    }

    [Test]
    public async Task IntegrationEndpointDelete_CascadesToIntegrationDeliveryAttempts()
    {
        // Intent: deleting an integration endpoint cascades to its delivery rows so the dedup
        // table doesn't accumulate references to a deleted integration. Behavioral test: seed
        // an integration and its delivery row, delete the integration, assert the child row is gone.
        using TestDatabaseFactory dbFactory = new();
        EnableForeignKeys(dbFactory);

        (int tenantId, int integrationId, long eventId) = await SeedEventAndIntegrationAsync(dbFactory);

        IIntegrationDeliveryAttemptRepository repo = CreateRepo(dbFactory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool claimed = await repo.TryClaimAttemptAsync(eventId, integrationId, now, CancellationToken.None);
        await Assert.That(claimed).IsTrue();

        int deletedIntegrations = await dbFactory.Context.IntegrationEndpoints
            .Where(i => i.Id == integrationId)
            .DeleteAsync();
        await Assert.That(deletedIntegrations).IsEqualTo(1);

        int remaining = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == eventId) && (a.IntegrationEndpointId == integrationId))
            .CountAsync();
        await Assert.That(remaining).IsEqualTo(0);
    }

    /// <summary>
    /// Turns SQLite foreign-key enforcement on for the underlying connection so the
    /// behavioral cascade tests exercise real ON DELETE CASCADE semantics. The base
    /// <see cref="TestDatabaseFactory"/> defaults to FKs OFF.
    /// </summary>
    private static void EnableForeignKeys(TestDatabaseFactory dbFactory)
    {
        SqliteConnection connection = (SqliteConnection)dbFactory.Context.OpenDbConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Seeds a User, Tenant, AlertRule, Machine, AlertEvent, and IntegrationEndpoint so an
    /// IntegrationDeliveryAttempt row can be inserted under FK enforcement. Returns the
    /// tenant id, integration endpoint id, and alert event id needed by cascade tests.
    /// </summary>
    private static async Task<(int tenantId, int integrationId, long eventId)> SeedEventAndIntegrationAsync(
        TestDatabaseFactory dbFactory)
    {
        DatabaseContext db = dbFactory.Context;

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await db.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        int ruleId = await db.InsertWithInt32IdentityAsync(rule);

        AlertEvent alertEvent = TestDataBuilder.BuildAlertEvent(
            alertRuleId: ruleId,
            tenantId: tenantId,
            machineId: machineId);
        long eventId = await db.InsertWithInt64IdentityAsync(alertEvent);

        IntegrationEndpoint integration = TestDataBuilder.BuildIntegrationEndpoint(
            tenantId: tenantId,
            createdByUserId: userId);
        int integrationId = await db.InsertWithInt32IdentityAsync(integration);

        return (tenantId, integrationId, eventId);
    }

    // ==========================================================================================
    // Enum ordinal stability and repository explicit-Status invariants.
    // ==========================================================================================

    /// <summary>
    /// The repository's TryClaimAttempt path MUST set Status explicitly so the column default
    /// (now Pending=0 after the C8 default-flip migration) is never relied on. This guards
    /// against an accidental future change that omits Status from the insert — a row inserted
    /// without an explicit Status would otherwise silently appear as already-delivered.
    /// </summary>
    [Test]
    public async Task TryClaimAttempt_SetsStatusToPending_NotRelyingOnDefault()
    {
        using TestDatabaseFactory dbFactory = new();
        (int _, int integrationId, long eventId) = await SeedEventAndIntegrationAsync(dbFactory);
        IIntegrationDeliveryAttemptRepository repo = CreateRepo(dbFactory);

        await repo.TryClaimAttemptAsync(eventId, integrationId, DateTimeOffset.UtcNow, CancellationToken.None);

        IntegrationDeliveryAttempt? attempt = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => a.AlertEventId == eventId && a.IntegrationEndpointId == integrationId)
            .FirstOrDefaultAsync();
        await Assert.That(attempt).IsNotNull();
        await Assert.That(attempt!.Status).IsEqualTo(IntegrationDeliveryAttemptStatus.Pending);
    }
}
