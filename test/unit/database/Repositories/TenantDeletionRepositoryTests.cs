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

    /// <summary>
    /// The set of ids produced by seeding a full operational-data graph for a single tenant, so
    /// tests can build child rows (scoped by parent FK, not TenantId) and verify per-table state.
    /// </summary>
    private sealed record TenantGraphIds(
        long MachineId,
        int AlertRuleId,
        int IntegrationEndpointId,
        long AlertEventId,
        int SigningKeyId);

    /// <summary>
    /// Seeds one row into every table touched by <see cref="ITenantDeletionRepository.PurgeTenantOperationalDataAsync"/>
    /// for the given tenant: the direct-TenantId tables plus the child tables scoped by their
    /// parent's TenantId (machine/rule/endpoint/event).
    /// </summary>
    private static async Task<TenantGraphIds> SeedTenantGraphAsync(TestDatabaseFactory dbFactory, int tenantId, int userId)
    {
        DatabaseContext db = dbFactory.Context;

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await db.InsertWithInt64IdentityAsync(machine);

        await db.InsertAsync(TestDataBuilder.BuildMachineTelemetry(machineId: machineId, tenantId: tenantId));
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: tenantId));
        await db.InsertAsync(new MachineStateDetail { MachineId = machineId });

        AlertRule alertRule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        int alertRuleId = await db.InsertWithInt32IdentityAsync(alertRule);

        await db.InsertAsync(new AlertRuleMachine
        {
            AlertRuleId = alertRuleId,
            MachineId = machineId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.InsertAsync(new AlertConditionState
        {
            AlertRuleId = alertRuleId,
            MachineId = machineId,
            FirstTriggeredAt = DateTimeOffset.UtcNow,
            LastObservedAt = DateTimeOffset.UtcNow,
        });

        AlertEvent alertEvent = TestDataBuilder.BuildAlertEvent(alertRuleId: alertRuleId, tenantId: tenantId, machineId: machineId);
        long alertEventId = await db.InsertWithInt64IdentityAsync(alertEvent);

        await db.InsertAsync(new AlertEmailDeliveryAttempt
        {
            AlertEventId = alertEventId,
            Recipient = "ops@example.com",
            Status = EmailDeliveryAttemptStatus.Succeeded,
            AttemptedAt = DateTimeOffset.UtcNow,
        });

        IntegrationEndpoint integrationEndpoint = TestDataBuilder.BuildIntegrationEndpoint(tenantId: tenantId, createdByUserId: userId);
        int integrationEndpointId = await db.InsertWithInt32IdentityAsync(integrationEndpoint);

        await db.InsertAsync(new IntegrationDeliveryAttempt
        {
            AlertEventId = alertEventId,
            IntegrationEndpointId = integrationEndpointId,
            Status = IntegrationDeliveryAttemptStatus.Succeeded,
            AttemptedAt = DateTimeOffset.UtcNow,
        });

        await db.InsertAsync(new RegistrationToken
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Test Token",
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            IsRevoked = false,
        });

        await db.InsertAsync(TestDataBuilder.BuildTenantOidcConfiguration(tenantId: tenantId));
        await db.InsertAsync(TestDataBuilder.BuildInvitation(tenantId: tenantId, invitedByUserId: userId));

        await db.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = tenantId,
            MachineLimit = 10,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.InsertAsync(TestDataBuilder.BuildSubscription(tenantId: tenantId));

        UserSigningKey signingKey = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        int signingKeyId = await db.InsertWithInt32IdentityAsync(signingKey);

        await db.InsertAsync(TestDataBuilder.BuildMachineAuthorizedKey(machineId: machineId, signingKeyId: signingKeyId, tenantId: tenantId, authorizedByUserId: userId));
        await db.InsertAsync(TestDataBuilder.BuildRemoteCommand(machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId));
        await db.InsertAsync(TestDataBuilder.BuildDataExportJob(tenantId: tenantId, requestedByUserId: userId));

        return new TenantGraphIds(machineId, alertRuleId, integrationEndpointId, alertEventId, signingKeyId);
    }

    private static async Task<int> CountAllSeededRowsAsync(DatabaseContext db, int tenantId, TenantGraphIds ids)
    {
        int count = 0;

        count += await db.Machines.CountAsync(m => m.TenantId == tenantId);
        count += await db.MachineTelemetry.CountAsync(x => x.TenantId == tenantId);
        count += await db.MachineStateSummaries.CountAsync(x => x.TenantId == tenantId);
        count += await db.MachineStateDetails.CountAsync(x => x.MachineId == ids.MachineId);
        count += await db.AlertRules.CountAsync(x => x.TenantId == tenantId);
        count += await db.AlertRuleMachines.CountAsync(x => x.AlertRuleId == ids.AlertRuleId);
        count += await db.AlertConditionStates.CountAsync(x => x.AlertRuleId == ids.AlertRuleId);
        count += await db.AlertEvents.CountAsync(x => x.TenantId == tenantId);
        count += await db.AlertEmailDeliveryAttempts.CountAsync(x => x.AlertEventId == ids.AlertEventId);
        count += await db.IntegrationEndpoints.CountAsync(x => x.TenantId == tenantId);
        count += await db.IntegrationDeliveryAttempts.CountAsync(x => x.IntegrationEndpointId == ids.IntegrationEndpointId);
        count += await db.RegistrationTokens.CountAsync(x => x.TenantId == tenantId);
        count += await db.TenantOidcConfigurations.CountAsync(x => x.TenantId == tenantId);
        count += await db.TenantInvitations.CountAsync(x => x.TenantId == tenantId);
        count += await db.TenantSubscriptionOverrides.CountAsync(x => x.TenantId == tenantId);
        count += await db.TenantSubscriptions.CountAsync(x => x.TenantId == tenantId);
        count += await db.UserSigningKeys.CountAsync(x => x.TenantId == tenantId);
        count += await db.MachineAuthorizedKeys.CountAsync(x => x.TenantId == tenantId);
        count += await db.RemoteCommands.CountAsync(x => x.TenantId == tenantId);
        count += await db.DataExportJobs.CountAsync(x => x.TenantId == tenantId);

        return count;
    }

    [Test]
    public async Task PurgeTenantOperationalDataAsync_DeletesEveryTenantScopedRowAndLeavesOtherTenantIntact()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant1 = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenant1Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant1);
        Tenant tenant2 = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenant2Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        TenantGraphIds tenant1Ids = await SeedTenantGraphAsync(dbFactory, tenant1Id, userId);
        TenantGraphIds tenant2Ids = await SeedTenantGraphAsync(dbFactory, tenant2Id, userId);

        await repo.PurgeTenantOperationalDataAsync(tenant1Id, CancellationToken.None);

        int tenant1RemainingRows = await CountAllSeededRowsAsync(dbFactory.Context, tenant1Id, tenant1Ids);
        await Assert.That(tenant1RemainingRows).IsEqualTo(0);

        int tenant2RemainingRows = await CountAllSeededRowsAsync(dbFactory.Context, tenant2Id, tenant2Ids);
        await Assert.That(tenant2RemainingRows).IsGreaterThan(0);

        // Tenant2's individual counts must match exactly what was seeded (nothing bled across).
        await Assert.That(await dbFactory.Context.Machines.CountAsync(m => m.TenantId == tenant2Id)).IsEqualTo(1);
        await Assert.That(await dbFactory.Context.AlertRules.CountAsync(x => x.TenantId == tenant2Id)).IsEqualTo(1);
        await Assert.That(await dbFactory.Context.IntegrationEndpoints.CountAsync(x => x.TenantId == tenant2Id)).IsEqualTo(1);

        // Tenants themselves and UserTenantRoles are never touched by the purge.
        await Assert.That(await dbFactory.Context.Tenants.CountAsync(t => t.Id == tenant1Id)).IsEqualTo(1);
        await Assert.That(await dbFactory.Context.UserAccounts.CountAsync(u => u.Id == userId)).IsEqualTo(1);
    }

    [Test]
    public async Task PurgeTenantOperationalDataAsync_RunTwice_SecondRunIsNoOpAndDoesNotThrow()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        TenantGraphIds ids = await SeedTenantGraphAsync(dbFactory, tenantId, userId);

        await repo.PurgeTenantOperationalDataAsync(tenantId, CancellationToken.None);
        await repo.PurgeTenantOperationalDataAsync(tenantId, CancellationToken.None);

        int remainingRows = await CountAllSeededRowsAsync(dbFactory.Context, tenantId, ids);
        await Assert.That(remainingRows).IsEqualTo(0);
    }

    [Test]
    public async Task SetTenantActiveAsync_Deactivates_UpdatesIsActiveDisabledAtAndDisabledByUserId()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        UserAccount operatorUser = TestDataBuilder.BuildUser();
        int operatorId = await dbFactory.Context.InsertWithInt32IdentityAsync(operatorUser);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: operatorId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        DateTimeOffset disabledAt = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
        await repo.SetTenantActiveAsync(tenantId, false, operatorId, disabledAt, CancellationToken.None);

        Tenant? row = await dbFactory.Context.Tenants.Where(t => t.Id == tenantId).FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.IsActive).IsFalse();
        await Assert.That(row.DisabledAt).IsEqualTo(disabledAt);
        await Assert.That(row.DisabledByUserId).IsEqualTo(operatorId);
    }

    [Test]
    public async Task SetTenantActiveAsync_Restores_ClearsDisabledFieldsAndSetsActive()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        UserAccount operatorUser = TestDataBuilder.BuildUser();
        int operatorId = await dbFactory.Context.InsertWithInt32IdentityAsync(operatorUser);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: operatorId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        await repo.SetTenantActiveAsync(tenantId, false, operatorId, DateTimeOffset.UtcNow, CancellationToken.None);
        await repo.SetTenantActiveAsync(tenantId, true, null, null, CancellationToken.None);

        Tenant? row = await dbFactory.Context.Tenants.Where(t => t.Id == tenantId).FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.IsActive).IsTrue();
        await Assert.That(row.DisabledAt).IsNull();
        await Assert.That(row.DisabledByUserId).IsNull();
    }

    [Test]
    public async Task GetUserIdsWithAnyRoleInTenantAsync_ReturnsDistinctMembersIncludingDisabledRoles()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        UserAccount admin = TestDataBuilder.BuildUser();
        int adminId = await dbFactory.Context.InsertWithInt32IdentityAsync(admin);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: adminId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        UserAccount activeMember = TestDataBuilder.BuildUser();
        int activeMemberId = await dbFactory.Context.InsertWithInt32IdentityAsync(activeMember);
        UserAccount disabledMember = TestDataBuilder.BuildUser();
        int disabledMemberId = await dbFactory.Context.InsertWithInt32IdentityAsync(disabledMember);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: activeMemberId, tenantId: tenantId, isActive: true));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: disabledMemberId, tenantId: tenantId, isActive: false));

        List<int> memberIds = await repo.GetUserIdsWithAnyRoleInTenantAsync(tenantId, CancellationToken.None);

        await Assert.That(memberIds.Count).IsEqualTo(2);
        await Assert.That(memberIds.Contains(activeMemberId)).IsTrue();
        await Assert.That(memberIds.Contains(disabledMemberId)).IsTrue();
    }

    [Test]
    public async Task DeleteUserTenantRolesForTenantAsync_RemovesOnlyTargetTenantsRoles()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant1 = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenant1Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant1);
        Tenant tenant2 = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenant2Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: tenant1Id));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: tenant2Id));

        await repo.DeleteUserTenantRolesForTenantAsync(tenant1Id, CancellationToken.None);

        await Assert.That(await dbFactory.Context.UserTenantRoles.CountAsync(r => r.AssignedTenantId == tenant1Id)).IsEqualTo(0);
        await Assert.That(await dbFactory.Context.UserTenantRoles.CountAsync(r => r.AssignedTenantId == tenant2Id)).IsEqualTo(1);
    }

    [Test]
    public async Task UserHasAnyActiveRoleAsync_UserWithRoleInOtherTenant_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant1 = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenant1Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant1);
        Tenant tenant2 = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenant2Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: tenant1Id, isActive: false));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: tenant2Id, isActive: true));

        bool hasActiveRole = await repo.UserHasAnyActiveRoleAsync(userId, CancellationToken.None);

        await Assert.That(hasActiveRole).IsTrue();
    }

    [Test]
    public async Task UserHasAnyActiveRoleAsync_OnlyDisabledRoleInSourceTenant_ReturnsFalseAfterRolesDeleted()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: tenantId, isActive: true));

        await repo.DeleteUserTenantRolesForTenantAsync(tenantId, CancellationToken.None);

        bool hasActiveRole = await repo.UserHasAnyActiveRoleAsync(userId, CancellationToken.None);

        await Assert.That(hasActiveRole).IsFalse();
    }

    [Test]
    public async Task MaskUserAsync_UnmaskedUser_MasksUsernameAndExternalIdAndReturnsOne()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        UserAccount user = TestDataBuilder.BuildUser(externalId: "google:12345", username: "orphan@example.com");
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        int rowsUpdated = await repo.MaskUserAsync(userId, CancellationToken.None);

        await Assert.That(rowsUpdated).IsEqualTo(1);

        UserAccount? row = await dbFactory.Context.UserAccounts.Where(u => u.Id == userId).FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Username).IsEqualTo($"deleted-user-{userId}");
        await Assert.That(row.ExternalId.StartsWith("deleted-user-")).IsTrue();
        await Assert.That(row.ExternalId).IsNotEqualTo("google:12345");
    }

    [Test]
    public async Task MaskUserAsync_SharedUserAcrossTenants_UntouchedUntilMasked()
    {
        // Intent: masking targets a specific orphaned user id — it must never mask a user who
        // still has membership elsewhere, which the caller enforces by only calling MaskUserAsync
        // for users UserHasAnyActiveRoleAsync reports as false for.
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        UserAccount sharedUser = TestDataBuilder.BuildUser(externalId: "google:shared", username: "shared@example.com");
        int sharedUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(sharedUser);

        UserAccount otherUser = TestDataBuilder.BuildUser(externalId: "google:other", username: "other@example.com");
        int otherUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(otherUser);

        int rowsUpdated = await repo.MaskUserAsync(otherUserId, CancellationToken.None);

        await Assert.That(rowsUpdated).IsEqualTo(1);

        UserAccount? sharedRow = await dbFactory.Context.UserAccounts.Where(u => u.Id == sharedUserId).FirstOrDefaultAsync();
        await Assert.That(sharedRow).IsNotNull();
        await Assert.That(sharedRow!.Username).IsEqualTo("shared@example.com");
        await Assert.That(sharedRow.ExternalId).IsEqualTo("google:shared");
    }

    [Test]
    public async Task MaskUserAsync_AlreadyMaskedUser_SecondCallReturnsZeroAndDoesNotChangeRow()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantDeletionRepository repo = CreateRepo(dbFactory);

        UserAccount user = TestDataBuilder.BuildUser(externalId: "google:12345", username: "orphan@example.com");
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        int firstResult = await repo.MaskUserAsync(userId, CancellationToken.None);
        await Assert.That(firstResult).IsEqualTo(1);

        UserAccount? afterFirst = await dbFactory.Context.UserAccounts.Where(u => u.Id == userId).FirstOrDefaultAsync();
        string usernameAfterFirst = afterFirst!.Username;
        string externalIdAfterFirst = afterFirst.ExternalId;

        int secondResult = await repo.MaskUserAsync(userId, CancellationToken.None);
        await Assert.That(secondResult).IsEqualTo(0);

        UserAccount? afterSecond = await dbFactory.Context.UserAccounts.Where(u => u.Id == userId).FirstOrDefaultAsync();
        await Assert.That(afterSecond!.Username).IsEqualTo(usernameAfterFirst);
        await Assert.That(afterSecond.ExternalId).IsEqualTo(externalIdAfterFirst);
    }
}
