// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Retention;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Unit tests for <see cref="DataCleanupService"/>.
/// Tests the batch hard-deletion of soft-deleted data across alert events,
/// audit log entries, remote commands, and expired data export jobs.
/// </summary>
public sealed class DataCleanupServiceTests
{
    private static ServerConfigurationService CreateConfigService()
    {
        return new ServerConfigurationService(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());
    }

    private static async Task InvokeCleanupAsync(DataCleanupService service)
    {
        System.Reflection.MethodInfo? method = typeof(DataCleanupService)
            .GetMethod("CleanupSoftDeletedRowsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;
    }

    private static DataCleanupService CreateService(TestServiceScopeFactory scopeFactory)
    {
        ILogger<DataCleanupService> logger = new NullLogger<DataCleanupService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));

        return new DataCleanupService(scopeFactory, CreateConfigService(), distributedLock, logger);
    }

    [Test]
    public async Task Cleanup_DeletesOldSoftDeletedAlertEvents()
    {
        using TestDatabaseFactory dbFactory = new();

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: 1);
        rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        // Soft-deleted 10 days ago — older than default 7-day grace period.
        await dbFactory.Context.InsertAsync(new AlertEvent
        {
            AlertRuleId = rule.Id,
            TenantId = 1,
            MachineId = machine.Id,
            Severity = AlertSeverity.Warning,
            Message = "Old alert",
            Status = AlertEventStatus.Resolved,
            TriggeredAt = DateTimeOffset.UtcNow.AddDays(-20),
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        // Soft-deleted 2 days ago — within grace period.
        await dbFactory.Context.InsertAsync(new AlertEvent
        {
            AlertRuleId = rule.Id,
            TenantId = 1,
            MachineId = machine.Id,
            Severity = AlertSeverity.Warning,
            Message = "Recent alert",
            Status = AlertEventStatus.Resolved,
            TriggeredAt = DateTimeOffset.UtcNow.AddDays(-10),
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-2)
        });

        // Active record (not deleted).
        await dbFactory.Context.InsertAsync(new AlertEvent
        {
            AlertRuleId = rule.Id,
            TenantId = 1,
            MachineId = machine.Id,
            Severity = AlertSeverity.Info,
            Message = "Active alert",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataCleanupService service = CreateService(scopeFactory);
        await InvokeCleanupAsync(service);

        List<AlertEvent> remaining = await dbFactory.Context.AlertEvents.ToListAsync();

        // Old soft-deleted should be gone, recent soft-deleted and active should remain.
        await Assert.That(remaining.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Cleanup_DeletesOldSoftDeletedAuditEntries()
    {
        using TestDatabaseFactory dbFactory = new();

        // Soft-deleted 10 days ago.
        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-20),
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        // Active record.
        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataCleanupService service = CreateService(scopeFactory);
        await InvokeCleanupAsync(service);

        List<AuditLogEntry> remaining = await dbFactory.Context.AuditLog.ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(1);
        await Assert.That(remaining[0].DeletedAt).IsNull();
    }

    [Test]
    public async Task Cleanup_DeletesExpiredDataExportJobs()
    {
        using TestDatabaseFactory dbFactory = new();

        // Expired export older than grace period.
        await dbFactory.Context.InsertAsync(new DataExportJob
        {
            TenantId = 1,
            Status = DataExportJobStatus.Expired,
            RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-20),
            ObjectKey = "old-export",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
            DownloadToken = "token-old"
        });

        // Expired export within grace period.
        await dbFactory.Context.InsertAsync(new DataExportJob
        {
            TenantId = 1,
            Status = DataExportJobStatus.Expired,
            RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-5),
            ObjectKey = "recent-export",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-2),
            DownloadToken = "token-recent"
        });

        // Active complete export.
        await dbFactory.Context.InsertAsync(new DataExportJob
        {
            TenantId = 1,
            Status = DataExportJobStatus.Complete,
            RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow,
            ObjectKey = "active-export",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            DownloadToken = "token-active"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataCleanupService service = CreateService(scopeFactory);
        await InvokeCleanupAsync(service);

        List<DataExportJob> remaining = await dbFactory.Context.DataExportJobs.ToListAsync();

        // Old expired export removed, recent expired and active should remain.
        await Assert.That(remaining.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Cleanup_EmptyTables_CompletesWithoutError()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataCleanupService service = CreateService(scopeFactory);

        await InvokeCleanupAsync(service);

        List<AlertEvent> alerts = await dbFactory.Context.AlertEvents.ToListAsync();
        await Assert.That(alerts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Cleanup_OnlySoftDeletedWithinGracePeriod_DeletesNothing()
    {
        using TestDatabaseFactory dbFactory = new();

        // Soft-deleted 2 days ago — within the 7-day grace period.
        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-10),
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-2)
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataCleanupService service = CreateService(scopeFactory);
        await InvokeCleanupAsync(service);

        List<AuditLogEntry> remaining = await dbFactory.Context.AuditLog.ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cleanup_LockNotAcquired_SkipsProcessing()
    {
        using TestDatabaseFactory dbFactory = new();

        // Insert a soft-deleted audit entry past grace period that would normally be cleaned up
        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-20),
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<DataCleanupService> logger = new NullLogger<DataCleanupService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();

        // Lock not acquired — returns null
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(null));
        DataCleanupService service = new(scopeFactory, CreateConfigService(), distributedLock, logger);

        System.Reflection.MethodInfo? executeMethod = typeof(DataCleanupService)
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
    }

    [Test]
    public async Task Cleanup_DeletesOldSoftDeletedRemoteCommands()
    {
        using TestDatabaseFactory dbFactory = new();

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        UserAccount user = TestDataBuilder.BuildUser();
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: user.Id, tenantId: 1);
        key.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(key);

        // Soft-deleted 10 days ago — older than default 7-day grace period.
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
            Timestamp = DateTimeOffset.UtcNow.AddDays(-20),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-19),
            Status = RemoteCommandStatus.Expired,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-20),
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        // Active remote command (not deleted).
        await dbFactory.Context.InsertAsync(new RemoteCommand
        {
            CommandId = Guid.NewGuid().ToString(),
            TenantId = 1,
            MachineId = machine.Id,
            UserId = user.Id,
            SigningKeyId = key.Id,
            CommandType = "reboot",
            Nonce = "nonce-active",
            Signature = "sig-active",
            CanonicalPayload = "{}",
            Timestamp = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            Status = RemoteCommandStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataCleanupService service = CreateService(scopeFactory);
        await InvokeCleanupAsync(service);

        List<RemoteCommand> remaining = await dbFactory.Context.RemoteCommands.ToListAsync();

        // Old soft-deleted command should be permanently removed, active should remain.
        await Assert.That(remaining.Count).IsEqualTo(1);
        await Assert.That(remaining[0].Nonce).IsEqualTo("nonce-active");
    }

    [Test]
    public async Task Cleanup_NothingToDelete_NoLogMessage()
    {
        using TestDatabaseFactory dbFactory = new();

        // Insert only active (non-deleted) records
        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<DataCleanupService> logger = Substitute.For<ILogger<DataCleanupService>>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));

        DataCleanupService service = new(scopeFactory, CreateConfigService(), distributedLock, logger);
        await InvokeCleanupAsync(service);

        // No Information log should fire since nothing was deleted
        logger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Active record should remain
        List<AuditLogEntry> remaining = await dbFactory.Context.AuditLog.ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cleanup_AllTableTypes_DeletedInSinglePass()
    {
        using TestDatabaseFactory dbFactory = new();

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: 1);
        rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        UserAccount user = TestDataBuilder.BuildUser();
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: user.Id, tenantId: 1);
        key.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(key);

        // Insert old soft-deleted data in all four tables
        await dbFactory.Context.InsertAsync(new AlertEvent
        {
            AlertRuleId = rule.Id,
            TenantId = 1,
            MachineId = machine.Id,
            Severity = AlertSeverity.Warning,
            Message = "Old alert",
            Status = AlertEventStatus.Resolved,
            TriggeredAt = DateTimeOffset.UtcNow.AddDays(-20),
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        await dbFactory.Context.InsertAsync(new AuditLogEntry
        {
            TenantId = 1,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow.AddDays(-20),
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-10)
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
            Timestamp = DateTimeOffset.UtcNow.AddDays(-20),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-19),
            Status = RemoteCommandStatus.Expired,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-20),
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        await dbFactory.Context.InsertAsync(new DataExportJob
        {
            TenantId = 1,
            Status = DataExportJobStatus.Expired,
            RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-20),
            ObjectKey = "old-export",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
            DownloadToken = "token-old"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        DataCleanupService service = CreateService(scopeFactory);
        await InvokeCleanupAsync(service);

        // All four soft-deleted records should be permanently removed
        List<AlertEvent> alertsRemaining = await dbFactory.Context.AlertEvents.ToListAsync();
        await Assert.That(alertsRemaining.Count).IsEqualTo(0);

        List<AuditLogEntry> auditRemaining = await dbFactory.Context.AuditLog.ToListAsync();
        await Assert.That(auditRemaining.Count).IsEqualTo(0);

        List<RemoteCommand> commandsRemaining = await dbFactory.Context.RemoteCommands.ToListAsync();
        await Assert.That(commandsRemaining.Count).IsEqualTo(0);

        List<DataExportJob> exportsRemaining = await dbFactory.Context.DataExportJobs.ToListAsync();
        await Assert.That(exportsRemaining.Count).IsEqualTo(0);
    }
}
