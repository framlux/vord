// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for alert event-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class AlertEventRepositoryTests
{
    /// <summary>
    /// Seeds a user, tenant, machine, and alert rule required by most alert event tests.
    /// Returns all generated IDs for downstream use.
    /// </summary>
    private static async Task<(int userId, int tenantId, long machineId, int ruleId)> SeedPrerequisitesAsync(
        TestDatabaseFactory dbFactory,
        int? tenantIdOverride = null)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = tenantIdOverride ?? await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        return (userId, tenantId, machineId, rule.Id);
    }

    // ========== CreateEventIfNotExistsAsync tests ==========

    [Test]
    public async Task CreateEventIfNotExistsAsync_ValidEvent_ReturnsEventWithId()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent alertEvent = TestDataBuilder.BuildAlertEvent(
            alertRuleId: ruleId,
            tenantId: tenantId,
            machineId: machineId);

        AlertEvent? result = await repo.CreateEventIfNotExistsAsync(alertEvent);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsNotEqualTo(0);
        await Assert.That(result.AlertRuleId).IsEqualTo(ruleId);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.MachineId).IsEqualTo(machineId);
        await Assert.That(result.Status).IsEqualTo(AlertEventStatus.Triggered);
    }

    [Test]
    public async Task CreateEventIfNotExistsAsync_DuplicateActiveEvent_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent firstEvent = TestDataBuilder.BuildAlertEvent(
            alertRuleId: ruleId,
            tenantId: tenantId,
            machineId: machineId);
        await repo.CreateEventIfNotExistsAsync(firstEvent);

        // Attempt to create a second active event for the same rule and machine.
        AlertEvent duplicateEvent = TestDataBuilder.BuildAlertEvent(
            alertRuleId: ruleId,
            tenantId: tenantId,
            machineId: machineId);

        AlertEvent? result = await repo.CreateEventIfNotExistsAsync(duplicateEvent);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task CreateEventIfNotExistsAsync_NullInput_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.CreateEventIfNotExistsAsync(null!))
            .ThrowsException()
            .And
            .IsTypeOf<ArgumentNullException>();
    }

    [Test]
    public async Task CreateEventIfNotExistsAsync_AcknowledgedEvent_BlocksNewDuplicateAlert()
    {
        // Intent: an alert event that has been acknowledged (operator has seen it but the
        // condition has not yet cleared) must NOT cause the next evaluation cycle to fire a
        // duplicate alert for the same rule/machine. Without this guard the on-call would be
        // re-paged every minute as long as the underlying condition persists. The deduplication
        // is keyed on "status != Resolved", which includes both Triggered and Acknowledged.
        // This is the regression test for that contract; the predecessor service had the same
        // behavior tested under a different name.
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        // Seed the first event, then acknowledge it via the repository's own API to mirror
        // production semantics.
        AlertEvent firstEvent = TestDataBuilder.BuildAlertEvent(
            alertRuleId: ruleId, tenantId: tenantId, machineId: machineId);
        AlertEvent? created = await repo.CreateEventIfNotExistsAsync(firstEvent);
        await Assert.That(created).IsNotNull();
        await repo.AcknowledgeAlertEventAsync(created!.Id, userId);

        // Attempt to create a new event for the same rule/machine while the prior one is still
        // Acknowledged (not Resolved). This must return null — no duplicate row, no new alert
        // delivery.
        AlertEvent duplicate = TestDataBuilder.BuildAlertEvent(
            alertRuleId: ruleId, tenantId: tenantId, machineId: machineId);
        AlertEvent? result = await repo.CreateEventIfNotExistsAsync(duplicate);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task CreateEventIfNotExistsAsync_ResolvedEventAllowsNew_ReturnsNewEvent()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        // Create and resolve the first event.
        AlertEvent firstEvent = TestDataBuilder.BuildAlertEvent(
            alertRuleId: ruleId,
            tenantId: tenantId,
            machineId: machineId);
        await repo.CreateEventIfNotExistsAsync(firstEvent);
        await repo.ResolveEventsForRuleMachineAsync(ruleId, machineId);

        // Creating a new event for the same rule and machine should succeed after resolution.
        AlertEvent newEvent = TestDataBuilder.BuildAlertEvent(
            alertRuleId: ruleId,
            tenantId: tenantId,
            machineId: machineId);

        AlertEvent? result = await repo.CreateEventIfNotExistsAsync(newEvent);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsNotEqualTo(0);
    }

    // ========== GetAlertEventsForTenantAsync tests ==========

    [Test]
    public async Task GetAlertEventsForTenantAsync_NoEvents_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<AlertEvent> result = await repo.GetAlertEventsForTenantAsync(99999, 0, 10, null, null);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetAlertEventsForTenantAsync_WithEvents_ReturnsOrderedByTriggeredAtDescending()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent older = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId, message: "Older");
        older.TriggeredAt = DateTimeOffset.UtcNow.AddHours(-2);
        older.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(older);

        AlertEvent newer = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId + 1, message: "Newer");
        newer.TriggeredAt = DateTimeOffset.UtcNow;
        newer.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(newer);

        List<AlertEvent> result = await repo.GetAlertEventsForTenantAsync(tenantId, 0, 10, null, null);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Message).IsEqualTo("Newer");
        await Assert.That(result[1].Message).IsEqualTo("Older");
    }

    [Test]
    public async Task GetAlertEventsForTenantAsync_Pagination_RespectsSkipAndTake()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        // Insert three events with distinct timestamps.
        for (int i = 0; i < 3; i++)
        {
            AlertEvent evt = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId + i, message: $"Event{i}");
            evt.TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-i);
            await dbFactory.Context.InsertWithInt64IdentityAsync(evt);
        }

        // Skip the first result, take one.
        List<AlertEvent> result = await repo.GetAlertEventsForTenantAsync(tenantId, 1, 1, null, null);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Message).IsEqualTo("Event1");
    }

    [Test]
    public async Task GetAlertEventsForTenantAsync_EmptyPage_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent evt = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId);
        await dbFactory.Context.InsertWithInt64IdentityAsync(evt);

        // Skip past all existing events.
        List<AlertEvent> result = await repo.GetAlertEventsForTenantAsync(tenantId, 100, 10, null, null);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetAlertEventsForTenantAsync_StatusFilter_ReturnsOnlyMatchingStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent triggered = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId, status: AlertEventStatus.Triggered);
        await dbFactory.Context.InsertWithInt64IdentityAsync(triggered);

        AlertEvent resolved = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId + 1, status: AlertEventStatus.Resolved);
        await dbFactory.Context.InsertWithInt64IdentityAsync(resolved);

        List<AlertEvent> result = await repo.GetAlertEventsForTenantAsync(tenantId, 0, 10, AlertEventStatus.Triggered, null);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(AlertEventStatus.Triggered);
    }

    [Test]
    public async Task GetAlertEventsForTenantAsync_SeverityFilter_ReturnsOnlyMatchingSeverity()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent warning = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId, severity: AlertSeverity.Warning);
        await dbFactory.Context.InsertWithInt64IdentityAsync(warning);

        AlertEvent critical = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId + 1, severity: AlertSeverity.Critical);
        await dbFactory.Context.InsertWithInt64IdentityAsync(critical);

        List<AlertEvent> result = await repo.GetAlertEventsForTenantAsync(tenantId, 0, 10, null, AlertSeverity.Critical);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Severity).IsEqualTo(AlertSeverity.Critical);
    }

    [Test]
    public async Task GetAlertEventsForTenantAsync_CombinedFilters_ReturnsOnlyMatchingBothFilters()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        // Triggered + Warning
        AlertEvent triggeredWarning = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId, status: AlertEventStatus.Triggered, severity: AlertSeverity.Warning);
        await dbFactory.Context.InsertWithInt64IdentityAsync(triggeredWarning);

        // Triggered + Critical
        AlertEvent triggeredCritical = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId + 1, status: AlertEventStatus.Triggered, severity: AlertSeverity.Critical);
        await dbFactory.Context.InsertWithInt64IdentityAsync(triggeredCritical);

        // Resolved + Critical
        AlertEvent resolvedCritical = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId + 2, status: AlertEventStatus.Resolved, severity: AlertSeverity.Critical);
        await dbFactory.Context.InsertWithInt64IdentityAsync(resolvedCritical);

        List<AlertEvent> result = await repo.GetAlertEventsForTenantAsync(tenantId, 0, 10, AlertEventStatus.Triggered, AlertSeverity.Critical);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(AlertEventStatus.Triggered);
        await Assert.That(result[0].Severity).IsEqualTo(AlertSeverity.Critical);
    }

    [Test]
    public async Task GetAlertEventsForTenantAsync_DifferentTenant_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent evt = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId);
        await dbFactory.Context.InsertWithInt64IdentityAsync(evt);

        List<AlertEvent> result = await repo.GetAlertEventsForTenantAsync(99999, 0, 10, null, null);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== CountAlertEventsForTenantAsync tests ==========

    [Test]
    public async Task CountAlertEventsForTenantAsync_NoEvents_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        int count = await repo.CountAlertEventsForTenantAsync(99999, null, null);

        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task CountAlertEventsForTenantAsync_MultipleEvents_ReturnsCorrectCount()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        for (int i = 0; i < 3; i++)
        {
            AlertEvent evt = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId + i);
            await dbFactory.Context.InsertWithInt64IdentityAsync(evt);
        }

        int count = await repo.CountAlertEventsForTenantAsync(tenantId, null, null);

        await Assert.That(count).IsEqualTo(3);
    }

    [Test]
    public async Task CountAlertEventsForTenantAsync_WithFilters_ReturnsFilteredCount()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent triggeredWarning = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId, status: AlertEventStatus.Triggered, severity: AlertSeverity.Warning);
        await dbFactory.Context.InsertWithInt64IdentityAsync(triggeredWarning);

        AlertEvent triggeredCritical = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId + 1, status: AlertEventStatus.Triggered, severity: AlertSeverity.Critical);
        await dbFactory.Context.InsertWithInt64IdentityAsync(triggeredCritical);

        AlertEvent resolvedCritical = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId + 2, status: AlertEventStatus.Resolved, severity: AlertSeverity.Critical);
        await dbFactory.Context.InsertWithInt64IdentityAsync(resolvedCritical);

        int count = await repo.CountAlertEventsForTenantAsync(tenantId, AlertEventStatus.Triggered, AlertSeverity.Critical);

        await Assert.That(count).IsEqualTo(1);
    }

    // ========== GetAlertEventByIdAsync (tenant-scoped) tests ==========

    [Test]
    public async Task GetAlertEventByIdAsync_TenantScoped_Found_ReturnsEvent()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent evt = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId);
        evt.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(evt);

        AlertEvent? result = await repo.GetAlertEventByIdAsync(evt.Id, tenantId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(evt.Id);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
    }

    [Test]
    public async Task GetAlertEventByIdAsync_TenantScoped_NotFound_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        AlertEvent? result = await repo.GetAlertEventByIdAsync(99999, 1);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetAlertEventByIdAsync_TenantScoped_WrongTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent evt = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId);
        evt.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(evt);

        AlertEvent? result = await repo.GetAlertEventByIdAsync(evt.Id, 99999);

        await Assert.That(result).IsNull();
    }

    // ========== GetAlertEventByIdAsync (unscoped) tests ==========

    [Test]
    public async Task GetAlertEventByIdAsync_Unscoped_Found_ReturnsEvent()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent evt = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId);
        evt.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(evt);

        AlertEvent? result = await repo.GetAlertEventByIdAsync(evt.Id);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(evt.Id);
    }

    [Test]
    public async Task GetAlertEventByIdAsync_Unscoped_NotFound_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        AlertEvent? result = await repo.GetAlertEventByIdAsync(99999);

        await Assert.That(result).IsNull();
    }

    // ========== AcknowledgeAlertEventAsync tests ==========

    [Test]
    public async Task AcknowledgeAlertEventAsync_ValidEvent_SetsStatusTimestampAndUserId()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent evt = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId);
        evt.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(evt);

        await repo.AcknowledgeAlertEventAsync(evt.Id, userId);

        AlertEvent? result = await repo.GetAlertEventByIdAsync(evt.Id);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Status).IsEqualTo(AlertEventStatus.Acknowledged);
        await Assert.That(result.AcknowledgedAt).IsNotNull();
        await Assert.That(result.AcknowledgedByUserId).IsEqualTo(userId);
    }

    [Test]
    public async Task AcknowledgeAlertEventAsync_InvalidEventId_DoesNotThrow()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        // Acknowledging a non-existent event should not throw; it simply updates zero rows.
        await repo.AcknowledgeAlertEventAsync(99999, 1);

        // Verify no side-effects by checking that the event still does not exist.
        AlertEvent? result = await repo.GetAlertEventByIdAsync(99999);

        await Assert.That(result).IsNull();
    }

    // ========== ResolveEventsForRuleMachineAsync tests ==========

    [Test]
    public async Task ResolveEventsForRuleMachineAsync_ResolvesActiveOnly_LeavesAlreadyResolved()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        // Insert an active (Triggered) event.
        AlertEvent active = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId, status: AlertEventStatus.Triggered);
        active.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(active);

        // Insert an already-resolved event with a known ResolvedAt timestamp.
        DateTimeOffset priorResolvedAt = DateTimeOffset.UtcNow.AddDays(-1);
        AlertEvent alreadyResolved = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId, status: AlertEventStatus.Resolved);
        alreadyResolved.ResolvedAt = priorResolvedAt;
        alreadyResolved.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(alreadyResolved);

        await repo.ResolveEventsForRuleMachineAsync(ruleId, machineId);

        AlertEvent? resolvedActive = await repo.GetAlertEventByIdAsync(active.Id);

        await Assert.That(resolvedActive).IsNotNull();
        await Assert.That(resolvedActive!.Status).IsEqualTo(AlertEventStatus.Resolved);
        await Assert.That(resolvedActive.ResolvedAt).IsNotNull();

        // The already-resolved event should remain unchanged.
        AlertEvent? unchangedResolved = await repo.GetAlertEventByIdAsync(alreadyResolved.Id);

        await Assert.That(unchangedResolved).IsNotNull();
        await Assert.That(unchangedResolved!.Status).IsEqualTo(AlertEventStatus.Resolved);
    }

    // ========== ResolveEventsForRuleAsync tests ==========

    [Test]
    public async Task ResolveEventsForRuleAsync_ResolvesAllActiveForRule()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        // Insert active events for two different machines under the same rule.
        AlertEvent event1 = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId, status: AlertEventStatus.Triggered);
        event1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(event1);

        AlertEvent event2 = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId + 1, status: AlertEventStatus.Acknowledged);
        event2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(event2);

        await repo.ResolveEventsForRuleAsync(ruleId);

        AlertEvent? resolved1 = await repo.GetAlertEventByIdAsync(event1.Id);
        AlertEvent? resolved2 = await repo.GetAlertEventByIdAsync(event2.Id);

        await Assert.That(resolved1).IsNotNull();
        await Assert.That(resolved1!.Status).IsEqualTo(AlertEventStatus.Resolved);
        await Assert.That(resolved1.ResolvedAt).IsNotNull();

        await Assert.That(resolved2).IsNotNull();
        await Assert.That(resolved2!.Status).IsEqualTo(AlertEventStatus.Resolved);
        await Assert.That(resolved2.ResolvedAt).IsNotNull();
    }

    // ========== HasActiveEventForRuleMachineAsync tests ==========

    [Test]
    public async Task HasActiveEventForRuleMachineAsync_ActiveTriggeredEvent_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent evt = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId, status: AlertEventStatus.Triggered);
        await dbFactory.Context.InsertWithInt64IdentityAsync(evt);

        bool result = await repo.HasActiveEventForRuleMachineAsync(ruleId, machineId);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task HasActiveEventForRuleMachineAsync_AcknowledgedEvent_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        // Acknowledged events are still active (not resolved).
        AlertEvent evt = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId, status: AlertEventStatus.Acknowledged);
        await dbFactory.Context.InsertWithInt64IdentityAsync(evt);

        bool result = await repo.HasActiveEventForRuleMachineAsync(ruleId, machineId);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task HasActiveEventForRuleMachineAsync_OnlyResolvedEvents_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int ruleId) = await SeedPrerequisitesAsync(dbFactory);

        AlertEvent evt = TestDataBuilder.BuildAlertEvent(alertRuleId: ruleId, tenantId: tenantId, machineId: machineId, status: AlertEventStatus.Resolved);
        await dbFactory.Context.InsertWithInt64IdentityAsync(evt);

        bool result = await repo.HasActiveEventForRuleMachineAsync(ruleId, machineId);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task HasActiveEventForRuleMachineAsync_NoEvents_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertEventRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        bool result = await repo.HasActiveEventForRuleMachineAsync(99999, 99999);

        await Assert.That(result).IsFalse();
    }
}
