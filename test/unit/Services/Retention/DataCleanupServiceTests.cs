// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

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
}
