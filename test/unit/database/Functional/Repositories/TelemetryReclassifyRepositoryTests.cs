// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for <c>ReclassifyTelemetryForTenantAsync</c>, the day-bounded UPDATE that moves a
/// tenant's surviving telemetry into the retention class its current subscription maps to. Runs
/// against a real SQLite database; the cross-partition row movement the same statement performs on
/// PostgreSQL is proven by the integration suite.
/// </summary>
public sealed class TelemetryReclassifyRepositoryTests
{
    private static readonly DateTimeOffset DayStart = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DayEnd = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

    private static Database.Repositories.DatabaseRepository RepoFor(TestDatabaseFactory dbFactory)
    {
        return new Database.Repositories.DatabaseRepository(
            dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
    }

    private static async Task<long> SeedTelemetryAsync(
        TestDatabaseFactory dbFactory, int tenantId, DateTimeOffset receivedAt, RetentionClass retentionClass)
    {
        MachineTelemetry row = TestDataBuilder.BuildMachineTelemetry(tenantId: tenantId, receivedAt: receivedAt);
        row.RetentionClass = retentionClass;

        return await dbFactory.Context.InsertWithInt64IdentityAsync(row);
    }

    private static async Task<RetentionClass> ClassOfAsync(TestDatabaseFactory dbFactory, long id)
    {
        MachineTelemetry row = await dbFactory.Context.MachineTelemetry.FirstAsync(t => t.Id == id);

        return row.RetentionClass;
    }

    [Test]
    public async Task Reclassify_RowInsideBounds_MovesToTargetClass()
    {
        using TestDatabaseFactory dbFactory = new();
        long id = await SeedTelemetryAsync(dbFactory, tenantId: 1, DayStart.AddHours(9), RetentionClass.Short);

        int moved = await RepoFor(dbFactory).ReclassifyTelemetryForTenantAsync(
            1, RetentionClass.Medium, DayStart, DayEnd, CancellationToken.None);

        await Assert.That(moved).IsEqualTo(1);
        await Assert.That(await ClassOfAsync(dbFactory, id)).IsEqualTo(RetentionClass.Medium);
    }

    [Test]
    public async Task Reclassify_RowAlreadyAtTarget_IsNotCountedOrRewritten()
    {
        // Intent: the "class differs from target" guard makes the move idempotent — a second pass over
        // an already-converged tenant touches no rows, so overlapping runs and Hangfire retries are safe.
        using TestDatabaseFactory dbFactory = new();
        await SeedTelemetryAsync(dbFactory, tenantId: 1, DayStart.AddHours(9), RetentionClass.Medium);

        int moved = await RepoFor(dbFactory).ReclassifyTelemetryForTenantAsync(
            1, RetentionClass.Medium, DayStart, DayEnd, CancellationToken.None);

        await Assert.That(moved).IsEqualTo(0);
    }

    [Test]
    public async Task Reclassify_RowBeforeLowerBound_IsNotMoved()
    {
        // Intent: a row older than the new effective window keeps its old class. On a downgrade those
        // rows expire on the old schedule, which preserves the accidental-downgrade undo.
        using TestDatabaseFactory dbFactory = new();
        long id = await SeedTelemetryAsync(dbFactory, tenantId: 1, DayStart.AddSeconds(-1), RetentionClass.Long);

        int moved = await RepoFor(dbFactory).ReclassifyTelemetryForTenantAsync(
            1, RetentionClass.Medium, DayStart, DayEnd, CancellationToken.None);

        await Assert.That(moved).IsEqualTo(0);
        await Assert.That(await ClassOfAsync(dbFactory, id)).IsEqualTo(RetentionClass.Long);
    }

    [Test]
    public async Task Reclassify_LowerBoundIsInclusive_UpperBoundIsExclusive()
    {
        // Intent: adjacent day chunks must neither skip nor double-count a row exactly on a boundary.
        using TestDatabaseFactory dbFactory = new();
        long onLowerBound = await SeedTelemetryAsync(dbFactory, tenantId: 1, DayStart, RetentionClass.Short);
        long onUpperBound = await SeedTelemetryAsync(dbFactory, tenantId: 1, DayEnd, RetentionClass.Short);

        int moved = await RepoFor(dbFactory).ReclassifyTelemetryForTenantAsync(
            1, RetentionClass.Medium, DayStart, DayEnd, CancellationToken.None);

        await Assert.That(moved).IsEqualTo(1);
        await Assert.That(await ClassOfAsync(dbFactory, onLowerBound)).IsEqualTo(RetentionClass.Medium);
        await Assert.That(await ClassOfAsync(dbFactory, onUpperBound)).IsEqualTo(RetentionClass.Short);
    }

    [Test]
    public async Task Reclassify_OtherTenantRows_AreNotTouched()
    {
        using TestDatabaseFactory dbFactory = new();
        long otherTenantRow = await SeedTelemetryAsync(dbFactory, tenantId: 2, DayStart.AddHours(9), RetentionClass.Short);

        int moved = await RepoFor(dbFactory).ReclassifyTelemetryForTenantAsync(
            1, RetentionClass.Medium, DayStart, DayEnd, CancellationToken.None);

        await Assert.That(moved).IsEqualTo(0);
        await Assert.That(await ClassOfAsync(dbFactory, otherTenantRow)).IsEqualTo(RetentionClass.Short);
    }
}
