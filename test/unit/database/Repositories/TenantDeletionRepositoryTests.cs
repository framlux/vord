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

public sealed class TenantDeletionRepositoryTests
{
    private static ITenantDeletionRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }

    private static TenantDeletion BuildDeletion(
        int tenantId,
        TenantDeletionStatus status = TenantDeletionStatus.Deactivated,
        DateTimeOffset? requestedAt = null,
        DateTimeOffset? scheduledPurgeAt = null,
        DateTimeOffset? purgedAt = null)
    {
        DateTimeOffset requested = requestedAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        return new TenantDeletion
        {
            TenantId = tenantId,
            TenantExternalId = $"tenant-{tenantId}",
            TenantName = $"Tenant {tenantId}",
            RequestedByUserId = 1,
            RequestedAt = requested,
            ScheduledPurgeAt = scheduledPurgeAt ?? requested.AddDays(30),
            Status = status,
            PurgedAt = purgedAt,
        };
    }

    [Test]
    public async Task InsertDeletionAsync_ValidRow_ReturnsRowWithIdAndPersistsAllFields()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        DateTimeOffset requestedAt = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        TenantDeletion deletion = BuildDeletion(tenantId: 7, requestedAt: requestedAt);
        deletion.Reason = "customer requested closure";

        TenantDeletion result = await repo.InsertDeletionAsync(deletion, CancellationToken.None);

        await Assert.That(result.Id).IsNotEqualTo(0);

        TenantDeletion? row = await dbFactory.Context.TenantDeletions
            .Where(d => d.Id == result.Id)
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.TenantId).IsEqualTo(7);
        await Assert.That(row.TenantExternalId).IsEqualTo("tenant-7");
        await Assert.That(row.TenantName).IsEqualTo("Tenant 7");
        await Assert.That(row.RequestedByUserId).IsEqualTo(1);
        await Assert.That(row.RequestedAt).IsEqualTo(requestedAt);
        await Assert.That(row.ScheduledPurgeAt).IsEqualTo(requestedAt.AddDays(30));
        await Assert.That(row.Status).IsEqualTo(TenantDeletionStatus.Deactivated);
        await Assert.That(row.Reason).IsEqualTo("customer requested closure");
        await Assert.That(row.PurgedAt).IsNull();
    }

    [Test]
    public async Task GetActiveDeletionForTenantAsync_DeactivatedRowExists_ReturnsRow()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        TenantDeletion deletion = BuildDeletion(tenantId: 10, status: TenantDeletionStatus.Deactivated);
        await dbFactory.Context.InsertAsync(deletion);

        TenantDeletion? result = await repo.GetActiveDeletionForTenantAsync(10, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.TenantId).IsEqualTo(10);
        await Assert.That(result.Status).IsEqualTo(TenantDeletionStatus.Deactivated);
    }

    [Test]
    public async Task GetActiveDeletionForTenantAsync_OnlyRestoredRow_ReturnsNull()
    {
        // Intent: a Restored row means the tenant is no longer under deletion. It must not be
        // reported as an "active" deletion, or a re-delete would be blocked incorrectly.
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        TenantDeletion deletion = BuildDeletion(tenantId: 11, status: TenantDeletionStatus.Restored);
        await dbFactory.Context.InsertAsync(deletion);

        TenantDeletion? result = await repo.GetActiveDeletionForTenantAsync(11, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetActiveDeletionForTenantAsync_PurgedRow_ReturnsRow()
    {
        // Intent: a Purged row is still "non-Restored" per the contract, and must be returned —
        // e.g. so a restore attempt against an already-purged tenant can be rejected upstream.
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        TenantDeletion deletion = BuildDeletion(tenantId: 12, status: TenantDeletionStatus.Purged, purgedAt: DateTimeOffset.UtcNow);
        await dbFactory.Context.InsertAsync(deletion);

        TenantDeletion? result = await repo.GetActiveDeletionForTenantAsync(12, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Status).IsEqualTo(TenantDeletionStatus.Purged);
    }

    [Test]
    public async Task GetActiveDeletionForTenantAsync_NoRows_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        TenantDeletion? result = await repo.GetActiveDeletionForTenantAsync(999, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task UpdateDeletionStatusAsync_ExistingRow_FlipsStatusAndStampsPurgedAtAndReturnsOne()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        TenantDeletion deletion = BuildDeletion(tenantId: 20, status: TenantDeletionStatus.Deactivated);
        int id = await dbFactory.Context.InsertWithInt32IdentityAsync(deletion);

        DateTimeOffset purgedAt = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        int updated = await repo.UpdateDeletionStatusAsync(id, TenantDeletionStatus.Purged, purgedAt, CancellationToken.None);

        await Assert.That(updated).IsEqualTo(1);

        TenantDeletion? row = await dbFactory.Context.TenantDeletions
            .Where(d => d.Id == id)
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Status).IsEqualTo(TenantDeletionStatus.Purged);
        await Assert.That(row.PurgedAt).IsEqualTo(purgedAt);
    }

    [Test]
    public async Task UpdateDeletionStatusAsync_MissingId_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        int updated = await repo.UpdateDeletionStatusAsync(12345, TenantDeletionStatus.Purged, DateTimeOffset.UtcNow, CancellationToken.None);

        await Assert.That(updated).IsEqualTo(0);
    }

    [Test]
    public async Task GetDueDeletionsAsync_ReturnsOnlyDeactivatedRowsAtOrBeforeNow()
    {
        // Intent: the purge job must pick up Deactivated rows whose schedule has arrived, and
        // must not pick up rows still in the future, already Purged, or Restored.
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

        TenantDeletion due = BuildDeletion(tenantId: 30, status: TenantDeletionStatus.Deactivated, scheduledPurgeAt: now.AddDays(-1));
        TenantDeletion dueExactly = BuildDeletion(tenantId: 31, status: TenantDeletionStatus.Deactivated, scheduledPurgeAt: now);
        TenantDeletion future = BuildDeletion(tenantId: 32, status: TenantDeletionStatus.Deactivated, scheduledPurgeAt: now.AddDays(1));
        TenantDeletion purged = BuildDeletion(tenantId: 33, status: TenantDeletionStatus.Purged, scheduledPurgeAt: now.AddDays(-1), purgedAt: now.AddDays(-1));
        TenantDeletion restored = BuildDeletion(tenantId: 34, status: TenantDeletionStatus.Restored, scheduledPurgeAt: now.AddDays(-1));

        await dbFactory.Context.InsertAsync(due);
        await dbFactory.Context.InsertAsync(dueExactly);
        await dbFactory.Context.InsertAsync(future);
        await dbFactory.Context.InsertAsync(purged);
        await dbFactory.Context.InsertAsync(restored);

        List<TenantDeletion> result = await repo.GetDueDeletionsAsync(now, CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.Any(d => d.TenantId == 30)).IsTrue();
        await Assert.That(result.Any(d => d.TenantId == 31)).IsTrue();
    }

    [Test]
    public async Task GetDueDeletionsAsync_NoneDue_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        DateTimeOffset now = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        TenantDeletion future = BuildDeletion(tenantId: 40, status: TenantDeletionStatus.Deactivated, scheduledPurgeAt: now.AddDays(1));
        await dbFactory.Context.InsertAsync(future);

        List<TenantDeletion> result = await repo.GetDueDeletionsAsync(now, CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListDeletionsAsync_IncludeCompletedFalse_ReturnsOnlyDeactivated()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        DateTimeOffset baseTime = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await dbFactory.Context.InsertAsync(BuildDeletion(tenantId: 50, status: TenantDeletionStatus.Deactivated, requestedAt: baseTime));
        await dbFactory.Context.InsertAsync(BuildDeletion(tenantId: 51, status: TenantDeletionStatus.Purged, requestedAt: baseTime.AddDays(1), purgedAt: baseTime.AddDays(2)));
        await dbFactory.Context.InsertAsync(BuildDeletion(tenantId: 52, status: TenantDeletionStatus.Restored, requestedAt: baseTime.AddDays(2)));

        (List<TenantDeletion> deletions, int totalCount) = await repo.ListDeletionsAsync(includeCompleted: false, skip: 0, take: 10, CancellationToken.None);

        await Assert.That(totalCount).IsEqualTo(1);
        await Assert.That(deletions.Count).IsEqualTo(1);
        await Assert.That(deletions[0].TenantId).IsEqualTo(50);
    }

    [Test]
    public async Task ListDeletionsAsync_IncludeCompletedTrue_ReturnsAllOrderedByRequestedAtDescending()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        DateTimeOffset baseTime = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await dbFactory.Context.InsertAsync(BuildDeletion(tenantId: 60, status: TenantDeletionStatus.Deactivated, requestedAt: baseTime));
        await dbFactory.Context.InsertAsync(BuildDeletion(tenantId: 61, status: TenantDeletionStatus.Purged, requestedAt: baseTime.AddDays(2), purgedAt: baseTime.AddDays(3)));
        await dbFactory.Context.InsertAsync(BuildDeletion(tenantId: 62, status: TenantDeletionStatus.Restored, requestedAt: baseTime.AddDays(1)));

        (List<TenantDeletion> deletions, int totalCount) = await repo.ListDeletionsAsync(includeCompleted: true, skip: 0, take: 10, CancellationToken.None);

        await Assert.That(totalCount).IsEqualTo(3);
        await Assert.That(deletions.Count).IsEqualTo(3);
        await Assert.That(deletions[0].TenantId).IsEqualTo(61);
        await Assert.That(deletions[1].TenantId).IsEqualTo(62);
        await Assert.That(deletions[2].TenantId).IsEqualTo(60);
    }

    [Test]
    public async Task ListDeletionsAsync_SkipAndTake_PagesResultsAndKeepsTotalCountAcrossPage()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        DateTimeOffset baseTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 5; i++)
        {
            await dbFactory.Context.InsertAsync(BuildDeletion(tenantId: 70 + i, status: TenantDeletionStatus.Deactivated, requestedAt: baseTime.AddDays(i)));
        }

        (List<TenantDeletion> deletions, int totalCount) = await repo.ListDeletionsAsync(includeCompleted: true, skip: 2, take: 2, CancellationToken.None);

        await Assert.That(totalCount).IsEqualTo(5);
        await Assert.That(deletions.Count).IsEqualTo(2);
        // Ordered by RequestedAt desc: [74, 73, 72, 71, 70] -> skip 2, take 2 -> [72, 71]
        await Assert.That(deletions[0].TenantId).IsEqualTo(72);
        await Assert.That(deletions[1].TenantId).IsEqualTo(71);
    }

    [Test]
    public async Task ListDeletionsAsync_NoRows_ReturnsEmptyListAndZeroTotal()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        (List<TenantDeletion> deletions, int totalCount) = await repo.ListDeletionsAsync(includeCompleted: true, skip: 0, take: 10, CancellationToken.None);

        await Assert.That(totalCount).IsEqualTo(0);
        await Assert.That(deletions.Count).IsEqualTo(0);
    }
}
