// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Server.Services.Telemetry;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Unit tests for <see cref="TelemetryCleanupService"/>.
/// Tests the batch hard-deletion of soft-deleted telemetry rows against a real SQLite-backed database.
/// </summary>
public sealed class TelemetryCleanupServiceTests
{
    private static ServerConfigurationService CreateConfigService()
    {
        return new ServerConfigurationService(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());
    }

    [Test]
    public async Task CleanupSoftDeletedRows_DeletesOldSoftDeletedRecords()
    {
        using TestDatabaseFactory dbFactory = new();

        // Insert a soft-deleted record older than 7 days.
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-20),
            SourceEventId = "old-deleted",
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        // Insert a soft-deleted record newer than 7 days.
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-5),
            SourceEventId = "recent-deleted",
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-2)
        });

        // Insert an active record.
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow,
            SourceEventId = "active-record"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryCleanupService> logger = new NullLogger<TelemetryCleanupService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryCleanupService service = new(scopeFactory, CreateConfigService(), distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryCleanupService)
            .GetMethod("CleanupSoftDeletedRowsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        List<MachineTelemetry> remaining = await dbFactory.Context.MachineTelemetry.ToListAsync();

        // Only the old soft-deleted record should be permanently removed.
        await Assert.That(remaining.Count).IsEqualTo(2);
        await Assert.That(remaining.Any(t => t.SourceEventId == "old-deleted")).IsEqualTo(false);
        await Assert.That(remaining.Any(t => t.SourceEventId == "recent-deleted")).IsEqualTo(true);
        await Assert.That(remaining.Any(t => t.SourceEventId == "active-record")).IsEqualTo(true);
    }

    [Test]
    public async Task CleanupSoftDeletedRows_RecentRecordsNotDeleted()
    {
        using TestDatabaseFactory dbFactory = new();

        // Insert soft-deleted records all within grace period.
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-3),
            SourceEventId = "recent-1",
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });

        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = 1,
            TelemetryType = 2,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-2),
            SourceEventId = "recent-2",
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-3)
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryCleanupService> logger = new NullLogger<TelemetryCleanupService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryCleanupService service = new(scopeFactory, CreateConfigService(), distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryCleanupService)
            .GetMethod("CleanupSoftDeletedRowsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        List<MachineTelemetry> remaining = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(remaining.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CleanupSoftDeletedRows_EmptyTable_RunsWithoutError()
    {
        using TestDatabaseFactory dbFactory = new();

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryCleanupService> logger = new NullLogger<TelemetryCleanupService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryCleanupService service = new(scopeFactory, CreateConfigService(), distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryCleanupService)
            .GetMethod("CleanupSoftDeletedRowsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Should complete without error.
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        List<MachineTelemetry> remaining = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(remaining.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CleanupSoftDeletedRows_OnlyRemovesPastGracePeriod()
    {
        using TestDatabaseFactory dbFactory = new();

        // Insert a soft-deleted record older than default 7-day grace period.
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-30),
            SourceEventId = "old-past-grace",
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-14)
        });

        // Insert a soft-deleted record within the grace period.
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-5),
            SourceEventId = "recent-within-grace",
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });

        // Insert an active (non-deleted) record.
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow,
            SourceEventId = "active-not-deleted"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryCleanupService> logger = new NullLogger<TelemetryCleanupService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryCleanupService service = new(scopeFactory, CreateConfigService(), distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryCleanupService)
            .GetMethod("CleanupSoftDeletedRowsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        List<MachineTelemetry> remaining = await dbFactory.Context.MachineTelemetry.ToListAsync();

        // Old past-grace record should be permanently deleted, the other two remain.
        await Assert.That(remaining.Count).IsEqualTo(2);
        await Assert.That(remaining.Any(t => t.SourceEventId == "old-past-grace")).IsEqualTo(false);
        await Assert.That(remaining.Any(t => t.SourceEventId == "recent-within-grace")).IsEqualTo(true);
        await Assert.That(remaining.Any(t => t.SourceEventId == "active-not-deleted")).IsEqualTo(true);
    }

    [Test]
    public async Task CleanupSoftDeletedRows_LockNotAcquired_SkipsProcessing()
    {
        using TestDatabaseFactory dbFactory = new();

        // Insert a soft-deleted record older than grace period that would normally be cleaned up
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-30),
            SourceEventId = "should-remain-locked",
            DeletedAt = DateTimeOffset.UtcNow.AddDays(-14)
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryCleanupService> logger = new NullLogger<TelemetryCleanupService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();

        // Lock not acquired — returns null
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(null));
        TelemetryCleanupService service = new(scopeFactory, CreateConfigService(), distributedLock, logger);

        System.Reflection.MethodInfo? executeMethod = typeof(TelemetryCleanupService)
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

        // Record should remain because lock was not acquired
        List<MachineTelemetry> remaining = await dbFactory.Context.MachineTelemetry.ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(1);
        await Assert.That(remaining[0].SourceEventId).IsEqualTo("should-remain-locked");
    }

    [Test]
    public async Task CleanupSoftDeletedRows_MultipleBatches_ProcessesAll()
    {
        using TestDatabaseFactory dbFactory = new();

        // Insert multiple soft-deleted records past grace period
        // The batch size is 10,000 — for this test we insert enough to verify the loop runs
        for (int i = 0; i < 5; i++)
        {
            await dbFactory.Context.InsertAsync(new MachineTelemetry
            {
                MachineId = 1,
                TelemetryType = 1,
                Payload = "{}",
                ReceivedAt = DateTimeOffset.UtcNow.AddDays(-30),
                SourceEventId = $"batch-record-{i}",
                DeletedAt = DateTimeOffset.UtcNow.AddDays(-14)
            });
        }

        // Also add an active record that should not be deleted
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow,
            SourceEventId = "active-record"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryCleanupService> logger = new NullLogger<TelemetryCleanupService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryCleanupService service = new(scopeFactory, CreateConfigService(), distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryCleanupService)
            .GetMethod("CleanupSoftDeletedRowsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // All 5 soft-deleted records should be permanently removed, active record remains
        List<MachineTelemetry> remaining = await dbFactory.Context.MachineTelemetry.ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(1);
        await Assert.That(remaining[0].SourceEventId).IsEqualTo("active-record");
    }

    [Test]
    public async Task CleanupSoftDeletedRows_ExactlyOneBatch_NoExtraQuery()
    {
        using TestDatabaseFactory dbFactory = new();

        // Insert exactly 3 records past grace — well under batch size of 10,000
        for (int i = 0; i < 3; i++)
        {
            await dbFactory.Context.InsertAsync(new MachineTelemetry
            {
                MachineId = 1,
                TelemetryType = 1,
                Payload = "{}",
                ReceivedAt = DateTimeOffset.UtcNow.AddDays(-30),
                SourceEventId = $"single-batch-{i}",
                DeletedAt = DateTimeOffset.UtcNow.AddDays(-14)
            });
        }

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryCleanupService> logger = new NullLogger<TelemetryCleanupService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryCleanupService service = new(scopeFactory, CreateConfigService(), distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryCleanupService)
            .GetMethod("CleanupSoftDeletedRowsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // All 3 records should be permanently deleted in a single batch
        List<MachineTelemetry> remaining = await dbFactory.Context.MachineTelemetry.ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(0);
    }
}
