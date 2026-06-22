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
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Repositories;

/// <summary>
/// Tests for <see cref="IAlertEventRepository"/> methods, with a focus on
/// <see cref="IAlertEventRepository.GetTriggeredEventsWithoutDeliveryAttemptsAsync"/>.
/// These run against an in-memory SQLite database via <see cref="TestDatabaseFactory"/>.
/// </summary>
public sealed class AlertEventRepositoryTests
{
    private static IAlertEventRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, NullLogger<DatabaseRepository>.Instance);
    }

    private static async Task<AlertEvent> SeedAlertEventAsync(
        DatabaseContext db,
        DateTimeOffset triggeredAt,
        AlertEventStatus status = AlertEventStatus.Triggered,
        int alertRuleId = 1,
        int tenantId = 1,
        long machineId = 1)
    {
        AlertEvent alertEvent = new()
        {
            AlertRuleId = alertRuleId,
            TenantId = tenantId,
            MachineId = machineId,
            Severity = AlertSeverity.Warning,
            Message = "Test alert",
            Details = null,
            Status = status,
            TriggeredAt = triggeredAt,
        };
        alertEvent.Id = await db.InsertWithInt64IdentityAsync(alertEvent);

        return alertEvent;
    }

    private static async Task SeedDeliveryAttemptAsync(
        DatabaseContext db,
        long alertEventId,
        int integrationEndpointId = 1,
        IntegrationDeliveryAttemptStatus status = IntegrationDeliveryAttemptStatus.Succeeded)
    {
        await db.InsertAsync(new IntegrationDeliveryAttempt
        {
            AlertEventId = alertEventId,
            IntegrationEndpointId = integrationEndpointId,
            Status = status,
            AttemptedAt = DateTimeOffset.UtcNow,
            SucceededAt = status == IntegrationDeliveryAttemptStatus.Succeeded ? DateTimeOffset.UtcNow : null,
        });
    }

    // ----- GetTriggeredEventsWithoutDeliveryAttemptsAsync -----

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_OrphanedInWindow_ReturnsEvent()
    {
        // Intent: the primary crash-window recovery path — a Triggered event with no delivery
        // attempt inside the re-drive window must be returned so AlertEvaluationJob can re-enqueue.
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = CreateRepo(dbFactory);

        DateTimeOffset triggeredAt = new(2026, 6, 1, 12, 10, 0, TimeSpan.Zero);
        AlertEvent orphan = await SeedAlertEventAsync(dbFactory.Context, triggeredAt);

        DateTimeOffset notBefore = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);   // 20 min earlier
        DateTimeOffset notAfter = new(2026, 6, 1, 12, 18, 0, TimeSpan.Zero);   // 8 min later

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == orphan.Id)).IsTrue();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_EventBeforeNotBefore_Excluded()
    {
        // Intent: events triggered before the lower bound (older than MaxAge) must be excluded.
        // Prevents perpetual re-driving for tenants that have no integrations configured.
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = CreateRepo(dbFactory);

        DateTimeOffset triggeredAt = new(2026, 6, 1, 11, 0, 0, TimeSpan.Zero); // 60 min before notAfter
        AlertEvent ancient = await SeedAlertEventAsync(dbFactory.Context, triggeredAt);

        DateTimeOffset notBefore = new(2026, 6, 1, 11, 45, 0, TimeSpan.Zero);
        DateTimeOffset notAfter = new(2026, 6, 1, 11, 58, 0, TimeSpan.Zero);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == ancient.Id)).IsFalse();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_EventAfterNotAfter_Excluded()
    {
        // Intent: events triggered after the upper bound (too recent, still in the live-enqueue
        // flight path) must be excluded to avoid racing with the normal delivery pipeline.
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = CreateRepo(dbFactory);

        DateTimeOffset triggeredAt = new(2026, 6, 1, 12, 25, 0, TimeSpan.Zero); // after notAfter
        AlertEvent recent = await SeedAlertEventAsync(dbFactory.Context, triggeredAt);

        DateTimeOffset notBefore = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset notAfter = new(2026, 6, 1, 12, 20, 0, TimeSpan.Zero);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == recent.Id)).IsFalse();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_SucceededAttemptPresent_Excluded()
    {
        // Intent: an event with a Succeeded delivery attempt must be excluded. It was delivered
        // successfully; re-enqueuing would cause the IntegrationDeliveryJob to attempt a second
        // HTTP POST to the receiver.
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = CreateRepo(dbFactory);

        DateTimeOffset triggeredAt = new(2026, 6, 1, 12, 10, 0, TimeSpan.Zero);
        AlertEvent delivered = await SeedAlertEventAsync(dbFactory.Context, triggeredAt);
        await SeedDeliveryAttemptAsync(dbFactory.Context, delivered.Id, integrationEndpointId: 7, IntegrationDeliveryAttemptStatus.Succeeded);

        DateTimeOffset notBefore = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset notAfter = new(2026, 6, 1, 12, 18, 0, TimeSpan.Zero);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == delivered.Id)).IsFalse();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_PendingAttemptPresent_Excluded()
    {
        // Intent: a Pending attempt means the delivery job is currently in-flight (claim inserted,
        // HTTP POST not yet completed). The anti-join must exclude it to avoid a second concurrent
        // delivery that would fight over the same TryClaimAttemptAsync unique constraint.
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = CreateRepo(dbFactory);

        DateTimeOffset triggeredAt = new(2026, 6, 1, 12, 10, 0, TimeSpan.Zero);
        AlertEvent inFlight = await SeedAlertEventAsync(dbFactory.Context, triggeredAt);
        await SeedDeliveryAttemptAsync(dbFactory.Context, inFlight.Id, integrationEndpointId: 3, IntegrationDeliveryAttemptStatus.Pending);

        DateTimeOffset notBefore = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset notAfter = new(2026, 6, 1, 12, 18, 0, TimeSpan.Zero);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == inFlight.Id)).IsFalse();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_ResolvedEvent_Excluded()
    {
        // Intent: Resolved events must never be re-driven. The status filter is the first gate.
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = CreateRepo(dbFactory);

        DateTimeOffset triggeredAt = new(2026, 6, 1, 12, 10, 0, TimeSpan.Zero);
        AlertEvent resolved = await SeedAlertEventAsync(dbFactory.Context, triggeredAt, AlertEventStatus.Resolved);

        DateTimeOffset notBefore = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset notAfter = new(2026, 6, 1, 12, 18, 0, TimeSpan.Zero);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == resolved.Id)).IsFalse();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_AcknowledgedEvent_Excluded()
    {
        // Intent: Acknowledged events must also be excluded. While they remain visible to users,
        // the delivery for the initial trigger was already processed (or deliberately skipped);
        // re-driving would cause unexpected notifications.
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = CreateRepo(dbFactory);

        DateTimeOffset triggeredAt = new(2026, 6, 1, 12, 10, 0, TimeSpan.Zero);
        AlertEvent acked = await SeedAlertEventAsync(dbFactory.Context, triggeredAt, AlertEventStatus.Acknowledged);

        DateTimeOffset notBefore = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset notAfter = new(2026, 6, 1, 12, 18, 0, TimeSpan.Zero);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == acked.Id)).IsFalse();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_OrderedByTriggeredAtAscending()
    {
        // Intent: results are ordered oldest-first so the re-drive pass has a deterministic
        // processing order and could reason about a partial-delivery watermark if needed.
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = CreateRepo(dbFactory);

        DateTimeOffset older = new(2026, 6, 1, 12, 5, 0, TimeSpan.Zero);
        DateTimeOffset newer = new(2026, 6, 1, 12, 14, 0, TimeSpan.Zero);

        AlertEvent olderEvent = await SeedAlertEventAsync(dbFactory.Context, older, machineId: 1);
        AlertEvent newerEvent = await SeedAlertEventAsync(dbFactory.Context, newer, machineId: 2);

        DateTimeOffset notBefore = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset notAfter = new(2026, 6, 1, 12, 18, 0, TimeSpan.Zero);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        List<AlertEvent> myResults = results.Where(e => (e.Id == olderEvent.Id) || (e.Id == newerEvent.Id)).ToList();
        await Assert.That(myResults.Count).IsEqualTo(2);
        await Assert.That(myResults[0].Id).IsEqualTo(olderEvent.Id);
        await Assert.That(myResults[1].Id).IsEqualTo(newerEvent.Id);
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_BoundaryNotAfter_InclusiveUpperBound()
    {
        // Intent: an event triggered at exactly notAfter must be included (inclusive upper bound).
        // An off-by-one (< instead of <=) would silently drop an event triggered at the exact
        // boundary, which is the most common real-world crash-window scenario.
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = CreateRepo(dbFactory);

        DateTimeOffset boundary = new(2026, 6, 1, 12, 18, 0, TimeSpan.Zero);
        AlertEvent atBoundary = await SeedAlertEventAsync(dbFactory.Context, boundary);

        DateTimeOffset notBefore = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(boundary, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == atBoundary.Id)).IsTrue();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_BoundaryNotBefore_InclusiveLowerBound()
    {
        // Intent: an event triggered at exactly notBefore must be included (inclusive lower bound).
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = CreateRepo(dbFactory);

        DateTimeOffset boundary = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        AlertEvent atBoundary = await SeedAlertEventAsync(dbFactory.Context, boundary);

        DateTimeOffset notAfter = new(2026, 6, 1, 12, 18, 0, TimeSpan.Zero);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, boundary, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == atBoundary.Id)).IsTrue();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_EmptyTable_ReturnsEmpty()
    {
        // Intent: when no events exist, the method must return an empty list rather than throwing.
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = CreateRepo(dbFactory);

        DateTimeOffset notBefore = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset notAfter = new(2026, 6, 1, 12, 18, 0, TimeSpan.Zero);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_MixedEvents_ReturnsOnlyOrphans()
    {
        // Intent: a realistic mix — one orphaned event, one with a delivery attempt, one too old,
        // one too recent — must yield exactly the orphaned event. This is the regression-catch
        // scenario for the anti-join + window filter composed correctly.
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = CreateRepo(dbFactory);
        DatabaseContext db = dbFactory.Context;

        DateTimeOffset notBefore = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset notAfter = new(2026, 6, 1, 12, 18, 0, TimeSpan.Zero);

        // Should be returned.
        AlertEvent orphan = await SeedAlertEventAsync(db, new DateTimeOffset(2026, 6, 1, 12, 10, 0, TimeSpan.Zero), machineId: 1);

        // Has a delivery attempt — should be excluded.
        AlertEvent delivered = await SeedAlertEventAsync(db, new DateTimeOffset(2026, 6, 1, 12, 11, 0, TimeSpan.Zero), machineId: 2);
        await SeedDeliveryAttemptAsync(db, delivered.Id, integrationEndpointId: 5);

        // Too old — should be excluded.
        AlertEvent ancient = await SeedAlertEventAsync(db, new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero), machineId: 3);

        // Too recent — should be excluded.
        AlertEvent recent = await SeedAlertEventAsync(db, new DateTimeOffset(2026, 6, 1, 12, 30, 0, TimeSpan.Zero), machineId: 4);

        // Resolved in window — should be excluded.
        AlertEvent resolved = await SeedAlertEventAsync(db, new DateTimeOffset(2026, 6, 1, 12, 12, 0, TimeSpan.Zero), AlertEventStatus.Resolved, machineId: 5);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == orphan.Id)).IsTrue();
        await Assert.That(results.Any(e => e.Id == delivered.Id)).IsFalse();
        await Assert.That(results.Any(e => e.Id == ancient.Id)).IsFalse();
        await Assert.That(results.Any(e => e.Id == recent.Id)).IsFalse();
        await Assert.That(results.Any(e => e.Id == resolved.Id)).IsFalse();
    }
}
