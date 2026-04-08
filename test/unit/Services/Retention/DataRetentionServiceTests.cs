// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Retention;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Unit tests for <see cref="DataRetentionService"/>.
/// Tests the per-tenant data purge logic for alert events, audit log entries,
/// and remote commands against a real SQLite-backed database.
/// </summary>
public sealed class DataRetentionServiceTests
{
    private static async Task InvokePurgeAsync(DataRetentionService service)
    {
        System.Reflection.MethodInfo? method = typeof(DataRetentionService)
            .GetMethod("PurgeOldDataAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;
    }

    private static DataRetentionService CreateService(TestServiceScopeFactory scopeFactory)
    {
        ILogger<DataRetentionService> logger = new NullLogger<DataRetentionService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));

        return new DataRetentionService(scopeFactory, distributedLock, logger);
    }

    [Test]
    public async Task PurgeOldData_SoftDeletesExpiredAlertEvents()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 7);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: 1);
        rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        // Old alert event (10 days ago).
        await dbFactory.Context.InsertAsync(new AlertEvent
        {
            AlertRuleId = rule.Id,
            TenantId = 1,
            MachineId = machine.Id,
            Severity = AlertSeverity.Warning,
            Message = "Old alert",
            Status = AlertEventStatus.Resolved,
            TriggeredAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        // Recent alert event (1 day ago).
        await dbFactory.Context.InsertAsync(new AlertEvent
        {
            AlertRuleId = rule.Id,
            TenantId = 1,
            MachineId = machine.Id,
            Severity = AlertSeverity.Warning,
            Message = "Recent alert",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow.AddDays(-1)
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataRetentionService service = CreateService(scopeFactory);
        await InvokePurgeAsync(service);

        List<AlertEvent> active = await dbFactory.Context.AlertEvents
            .Where(e => e.DeletedAt == null).ToListAsync();
        await Assert.That(active.Count).IsEqualTo(1);
        await Assert.That(active[0].Message).IsEqualTo("Recent alert");

        List<AlertEvent> softDeleted = await dbFactory.Context.AlertEvents
            .Where(e => e.DeletedAt != null).ToListAsync();
        await Assert.That(softDeleted.Count).IsEqualTo(1);
        await Assert.That(softDeleted[0].Message).IsEqualTo("Old alert");
    }

    [Test]
    public async Task PurgeOldData_SoftDeletesExpiredAuditLogEntries()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 7);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        // Old audit entry (10 days ago).
        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-10)
        });

        // Recent audit entry (1 day ago).
        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-1)
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataRetentionService service = CreateService(scopeFactory);
        await InvokePurgeAsync(service);

        List<AuditLogEntry> active = await dbFactory.Context.AuditLog
            .Where(e => e.DeletedAt == null).ToListAsync();
        await Assert.That(active.Count).IsEqualTo(1);

        List<AuditLogEntry> softDeleted = await dbFactory.Context.AuditLog
            .Where(e => e.DeletedAt != null).ToListAsync();
        await Assert.That(softDeleted.Count).IsEqualTo(1);
    }

    [Test]
    public async Task PurgeOldData_SoftDeletesExpiredRemoteCommands()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 7);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        UserAccount user = TestDataBuilder.BuildUser();
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: user.Id, tenantId: 1);
        key.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(key);

        // Old remote command (10 days ago).
        await dbFactory.Context.InsertAsync(new RemoteCommand
        {
            CommandId = Guid.NewGuid().ToString(),
            TenantId = 1,
            MachineId = machine.Id,
            UserId = user.Id,
            SigningKeyId = key.Id,
            CommandType = "reboot",
            Nonce = "nonce-old",
            Signature = "sig-old",
            CanonicalPayload = "{}",
            Timestamp = DateTimeOffset.UtcNow.AddDays(-10),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-9),
            Status = RemoteCommandStatus.Expired,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        // Recent remote command (1 day ago).
        await dbFactory.Context.InsertAsync(new RemoteCommand
        {
            CommandId = Guid.NewGuid().ToString(),
            TenantId = 1,
            MachineId = machine.Id,
            UserId = user.Id,
            SigningKeyId = key.Id,
            CommandType = "reboot",
            Nonce = "nonce-recent",
            Signature = "sig-recent",
            CanonicalPayload = "{}",
            Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            Status = RemoteCommandStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataRetentionService service = CreateService(scopeFactory);
        await InvokePurgeAsync(service);

        List<RemoteCommand> active = await dbFactory.Context.RemoteCommands
            .Where(c => c.DeletedAt == null).ToListAsync();
        await Assert.That(active.Count).IsEqualTo(1);
        await Assert.That(active[0].Nonce).IsEqualTo("nonce-recent");

        List<RemoteCommand> softDeleted = await dbFactory.Context.RemoteCommands
            .Where(c => c.DeletedAt != null).ToListAsync();
        await Assert.That(softDeleted.Count).IsEqualTo(1);
    }

    [Test]
    public async Task PurgeOldData_RespectsPerTenantRetention()
    {
        using TestDatabaseFactory dbFactory = new();

        // Tenant 1: 30-day retention.
        TenantSubscription sub1 = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 30);
        sub1.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub1);

        // Tenant 2: 1-day retention.
        TenantSubscription sub2 = TestDataBuilder.BuildSubscription(tenantId: 2, retentionDays: 1);
        sub2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub2);

        // Insert 5-day-old audit entries for both.
        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-5)
        });

        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 2,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-5)
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataRetentionService service = CreateService(scopeFactory);
        await InvokePurgeAsync(service);

        // Tenant 1 (30-day retention): should be active.
        // Tenant 2 (1-day retention): should be soft-deleted.
        List<AuditLogEntry> active = await dbFactory.Context.AuditLog
            .Where(e => e.DeletedAt == null).ToListAsync();
        await Assert.That(active.Count).IsEqualTo(1);
        await Assert.That(active[0].TenantId).IsEqualTo(1);
    }

    [Test]
    public async Task PurgeOldData_SkipsAlreadySoftDeletedRecords()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 7);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        DateTimeOffset originalDeletedAt = DateTimeOffset.UtcNow.AddDays(-3);

        // Insert an already soft-deleted audit entry.
        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-10),
            DeletedAt = originalDeletedAt
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataRetentionService service = CreateService(scopeFactory);
        await InvokePurgeAsync(service);

        // The already soft-deleted row should remain unchanged.
        List<AuditLogEntry> all = await dbFactory.Context.AuditLog.ToListAsync();
        await Assert.That(all.Count).IsEqualTo(1);
        await Assert.That(all[0].DeletedAt).IsNotNull();
    }

    [Test]
    public async Task PurgeOldData_NoSubscriptions_CompletesWithoutError()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataRetentionService service = CreateService(scopeFactory);

        await InvokePurgeAsync(service);

        List<AlertEvent> alerts = await dbFactory.Context.AlertEvents.ToListAsync();
        await Assert.That(alerts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PurgeOldData_AllRecentData_DeletesNothing()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 7);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddHours(-6)
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataRetentionService service = CreateService(scopeFactory);
        await InvokePurgeAsync(service);

        List<AuditLogEntry> all = await dbFactory.Context.AuditLog.ToListAsync();
        await Assert.That(all.Count).IsEqualTo(1);
        await Assert.That(all[0].DeletedAt).IsNull();
    }

    [Test]
    public async Task PurgeOldData_LockNotAcquired_SkipsProcessing()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 1);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        // Insert an old audit entry that would normally be soft-deleted
        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-10)
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<DataRetentionService> logger = new NullLogger<DataRetentionService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();

        // Lock not acquired — returns null
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(null));
        DataRetentionService service = new(scopeFactory, distributedLock, logger);

        System.Reflection.MethodInfo? executeMethod = typeof(DataRetentionService)
            .GetMethod("ExecuteAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        try
        {
            await (Task)executeMethod!.Invoke(service, [cts.Token])!;
        }
        catch (OperationCanceledException)
        {
            // Expected — the service loop exits via cancellation
        }

        // Audit entry should remain because lock was not acquired
        List<AuditLogEntry> remaining = await dbFactory.Context.AuditLog.ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(1);
        await Assert.That(remaining[0].DeletedAt).IsNull();
    }

    [Test]
    public async Task PurgeOldData_MultipleDataTypes_AllSoftDeleted()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 7);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: 1);
        rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        UserAccount user = TestDataBuilder.BuildUser();
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: user.Id, tenantId: 1);
        key.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(key);

        // Insert old data in all three tables
        await dbFactory.Context.InsertAsync(new AlertEvent
        {
            AlertRuleId = rule.Id,
            TenantId = 1,
            MachineId = machine.Id,
            Severity = AlertSeverity.Warning,
            Message = "Old alert",
            Status = AlertEventStatus.Resolved,
            TriggeredAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-10)
        });

        await dbFactory.Context.InsertAsync(new RemoteCommand
        {
            CommandId = Guid.NewGuid().ToString(),
            TenantId = 1,
            MachineId = machine.Id,
            UserId = user.Id,
            SigningKeyId = key.Id,
            CommandType = "reboot",
            Nonce = "nonce-old",
            Signature = "sig-old",
            CanonicalPayload = "{}",
            Timestamp = DateTimeOffset.UtcNow.AddDays(-10),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-9),
            Status = RemoteCommandStatus.Expired,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataRetentionService service = CreateService(scopeFactory);
        await InvokePurgeAsync(service);

        // All three should be soft-deleted
        List<AlertEvent> softDeletedAlerts = await dbFactory.Context.AlertEvents
            .Where(e => e.DeletedAt != null).ToListAsync();
        await Assert.That(softDeletedAlerts.Count).IsEqualTo(1);

        List<AuditLogEntry> softDeletedAudit = await dbFactory.Context.AuditLog
            .Where(e => e.DeletedAt != null).ToListAsync();
        await Assert.That(softDeletedAudit.Count).IsEqualTo(1);

        List<RemoteCommand> softDeletedCommands = await dbFactory.Context.RemoteCommands
            .Where(c => c.DeletedAt != null).ToListAsync();
        await Assert.That(softDeletedCommands.Count).IsEqualTo(1);
    }

    [Test]
    public async Task PurgeOldData_OnlyActiveAndCanceledAndPastDueSubscriptions_Processed()
    {
        using TestDatabaseFactory dbFactory = new();

        // Create a subscription with None status — should not be queried
        TenantSubscription noneSub = TestDataBuilder.BuildSubscription(
            tenantId: 1,
            retentionDays: 1,
            status: SubscriptionStatus.None);
        noneSub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(noneSub);

        // Insert an old audit entry for that tenant
        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-10)
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataRetentionService service = CreateService(scopeFactory);
        await InvokePurgeAsync(service);

        // The audit entry should remain unchanged because None-status subscriptions are not processed
        List<AuditLogEntry> all = await dbFactory.Context.AuditLog.ToListAsync();
        await Assert.That(all.Count).IsEqualTo(1);
        await Assert.That(all[0].DeletedAt).IsNull();
    }
}
