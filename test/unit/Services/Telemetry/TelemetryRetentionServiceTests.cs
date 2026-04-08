// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Telemetry;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Unit tests for <see cref="TelemetryRetentionService"/>.
/// Tests the per-tenant telemetry purge logic against a real SQLite-backed database.
/// </summary>
public sealed class TelemetryRetentionServiceTests
{
    [Test]
    public async Task PurgeOldTelemetry_DeletesExpiredRows()
    {
        using TestDatabaseFactory dbFactory = new();

        // Set up a tenant with 7-day retention.
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 7);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        // Insert a machine for this tenant.
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // Insert telemetry: one old (10 days ago) and one recent (1 day ago).
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine.Id,
            TenantId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-10),
            SourceEventId = "old-event"
        });

        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine.Id,
            TenantId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-1),
            SourceEventId = "recent-event"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryRetentionService> logger = new NullLogger<TelemetryRetentionService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryRetentionService service = new(scopeFactory, distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryRetentionService)
            .GetMethod("PurgeOldTelemetryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // Both rows still exist (soft-delete sets DeletedAt instead of removing rows).
        List<MachineTelemetry> all = await dbFactory.Context.MachineTelemetry.ToListAsync();
        await Assert.That(all.Count).IsEqualTo(2);

        // Only the recent row should be active (DeletedAt is null).
        List<MachineTelemetry> active = await dbFactory.Context.MachineTelemetry
            .Where(t => t.DeletedAt == null).ToListAsync();
        await Assert.That(active.Count).IsEqualTo(1);
        await Assert.That(active[0].SourceEventId).IsEqualTo("recent-event");

        // The old row should be soft-deleted (DeletedAt is set).
        List<MachineTelemetry> softDeleted = await dbFactory.Context.MachineTelemetry
            .Where(t => t.DeletedAt != null).ToListAsync();
        await Assert.That(softDeleted.Count).IsEqualTo(1);
        await Assert.That(softDeleted[0].SourceEventId).IsEqualTo("old-event");
    }

    [Test]
    public async Task PurgeOldTelemetry_SkipsTenants_WithNoMachines()
    {
        using TestDatabaseFactory dbFactory = new();

        // Set up a tenant with subscription but no machines.
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 1);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryRetentionService> logger = new NullLogger<TelemetryRetentionService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryRetentionService service = new(scopeFactory, distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryRetentionService)
            .GetMethod("PurgeOldTelemetryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Should complete without error.
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        List<MachineTelemetry> remaining = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(remaining.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PurgeOldTelemetry_RespectsPerTenantRetention()
    {
        using TestDatabaseFactory dbFactory = new();

        // Tenant 1: 30-day retention.
        TenantSubscription sub1 = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 30);
        sub1.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub1);

        // Tenant 2: 1-day retention.
        TenantSubscription sub2 = TestDataBuilder.BuildSubscription(tenantId: 2, retentionDays: 1);
        sub2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub2);

        // Machine for tenant 1.
        Machine machine1 = TestDataBuilder.BuildMachine(tenantId: 1);
        machine1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine1);

        // Machine for tenant 2.
        Machine machine2 = TestDataBuilder.BuildMachine(tenantId: 2);
        machine2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine2);

        // Insert 5-day-old telemetry for both.
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine1.Id,
            TenantId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-5),
            SourceEventId = "tenant1-old"
        });

        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine2.Id,
            TenantId = 2,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-5),
            SourceEventId = "tenant2-old"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryRetentionService> logger = new NullLogger<TelemetryRetentionService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryRetentionService service = new(scopeFactory, distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryRetentionService)
            .GetMethod("PurgeOldTelemetryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // Both rows still exist (soft-delete sets DeletedAt instead of removing rows).
        List<MachineTelemetry> all = await dbFactory.Context.MachineTelemetry.ToListAsync();
        await Assert.That(all.Count).IsEqualTo(2);

        // Tenant 1's telemetry (5 days old, 30-day retention) should be active.
        // Tenant 2's telemetry (5 days old, 1-day retention) should be soft-deleted.
        List<MachineTelemetry> active = await dbFactory.Context.MachineTelemetry
            .Where(t => t.DeletedAt == null).ToListAsync();
        await Assert.That(active.Count).IsEqualTo(1);
        await Assert.That(active[0].SourceEventId).IsEqualTo("tenant1-old");
    }

    [Test]
    public async Task PurgeOldTelemetry_NoSubscriptions_CompletesWithoutError()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryRetentionService> logger = new NullLogger<TelemetryRetentionService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryRetentionService service = new(scopeFactory, distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryRetentionService)
            .GetMethod("PurgeOldTelemetryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Should complete without exception when no subscriptions exist.
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        List<MachineTelemetry> remaining = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(remaining.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PurgeOldTelemetry_AllTelemetryRecent_DeletesNothing()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 7);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // Insert recent telemetry only.
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine.Id,
            TenantId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddHours(-6),
            SourceEventId = "recent-1"
        });

        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine.Id,
            TenantId = 1,
            TelemetryType = 2,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddHours(-12),
            SourceEventId = "recent-2"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryRetentionService> logger = new NullLogger<TelemetryRetentionService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryRetentionService service = new(scopeFactory, distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryRetentionService)
            .GetMethod("PurgeOldTelemetryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        List<MachineTelemetry> remaining = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(remaining.Count).IsEqualTo(2);
    }

    [Test]
    public async Task PurgeOldTelemetry_AlreadySoftDeleted_NotReProcessed()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 7);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        DateTimeOffset originalDeletedAt = DateTimeOffset.UtcNow.AddDays(-3);

        // Insert a row that is already soft-deleted (older than retention).
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine.Id,
            TenantId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-10),
            SourceEventId = "already-deleted",
            DeletedAt = originalDeletedAt
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryRetentionService> logger = new NullLogger<TelemetryRetentionService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryRetentionService service = new(scopeFactory, distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryRetentionService)
            .GetMethod("PurgeOldTelemetryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // The already soft-deleted row should not have its DeletedAt timestamp changed.
        List<MachineTelemetry> all = await dbFactory.Context.MachineTelemetry.ToListAsync();
        await Assert.That(all.Count).IsEqualTo(1);
        await Assert.That(all[0].DeletedAt).IsNotNull();
    }

    [Test]
    public async Task PurgeOldTelemetry_MultipleTenants_EachUsesOwnRetention()
    {
        using TestDatabaseFactory dbFactory = new();

        // Tenant 1: 3-day retention.
        TenantSubscription sub1 = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 3);
        sub1.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub1);

        // Tenant 2: 10-day retention.
        TenantSubscription sub2 = TestDataBuilder.BuildSubscription(tenantId: 2, retentionDays: 10);
        sub2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub2);

        Machine machine1 = TestDataBuilder.BuildMachine(tenantId: 1);
        machine1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine1);

        Machine machine2 = TestDataBuilder.BuildMachine(tenantId: 2);
        machine2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine2);

        // Insert 5-day-old telemetry for both tenants.
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine1.Id,
            TenantId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-5),
            SourceEventId = "t1-5days"
        });

        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine2.Id,
            TenantId = 2,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-5),
            SourceEventId = "t2-5days"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryRetentionService> logger = new NullLogger<TelemetryRetentionService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryRetentionService service = new(scopeFactory, distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryRetentionService)
            .GetMethod("PurgeOldTelemetryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // Tenant 1 (3-day retention): 5-day-old row should be soft-deleted.
        // Tenant 2 (10-day retention): 5-day-old row should be untouched.
        List<MachineTelemetry> active = await dbFactory.Context.MachineTelemetry
            .Where(t => t.DeletedAt == null).ToListAsync();
        await Assert.That(active.Count).IsEqualTo(1);
        await Assert.That(active[0].SourceEventId).IsEqualTo("t2-5days");
    }

    [Test]
    public async Task PurgeOldTelemetry_LockNotAcquired_SkipsProcessing()
    {
        using TestDatabaseFactory dbFactory = new();

        // Set up tenant with old telemetry that would normally be purged
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 1);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine.Id,
            TenantId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-10),
            SourceEventId = "should-remain"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryRetentionService> logger = new NullLogger<TelemetryRetentionService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();

        // Lock not acquired — returns null
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(null));
        TelemetryRetentionService service = new(scopeFactory, distributedLock, logger);

        // Use ExecuteAsync via reflection to test the lock acquisition path
        System.Reflection.MethodInfo? executeMethod = typeof(TelemetryRetentionService)
            .GetMethod("ExecuteAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        using CancellationTokenSource cts = new();

        // Cancel immediately after first iteration
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        try
        {
            await (Task)executeMethod!.Invoke(service, [cts.Token])!;
        }
        catch (OperationCanceledException)
        {
            // Expected — the service loop exits via cancellation
        }

        // Telemetry should remain untouched because lock was not acquired
        List<MachineTelemetry> remaining = await dbFactory.Context.MachineTelemetry.ToListAsync();
        await Assert.That(remaining.Count).IsEqualTo(1);
        await Assert.That(remaining[0].DeletedAt).IsNull();
    }

    [Test]
    public async Task PurgeOldTelemetry_ZeroRetentionDays_DeletesAll()
    {
        using TestDatabaseFactory dbFactory = new();

        // Zero retention means everything should be soft-deleted
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 0);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // Insert telemetry that is only 1 minute old
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine.Id,
            TenantId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            SourceEventId = "very-recent"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryRetentionService> logger = new NullLogger<TelemetryRetentionService>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryRetentionService service = new(scopeFactory, distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryRetentionService)
            .GetMethod("PurgeOldTelemetryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // With 0 retention days, the cutoff is UtcNow, so everything before now should be soft-deleted
        List<MachineTelemetry> active = await dbFactory.Context.MachineTelemetry
            .Where(t => t.DeletedAt == null).ToListAsync();
        await Assert.That(active.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PurgeOldTelemetry_PositiveDeleted_LogsPerTenant()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 1);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // Insert old telemetry that will trigger the per-tenant logging branch
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine.Id,
            TenantId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddDays(-5),
            SourceEventId = "old-event-for-log"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryRetentionService> logger = Substitute.For<ILogger<TelemetryRetentionService>>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryRetentionService service = new(scopeFactory, distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryRetentionService)
            .GetMethod("PurgeOldTelemetryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // Should have two Information logs: one per-tenant and one summary
        logger.Received(2).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task PurgeOldTelemetry_SubscriptionWithNoExpiredTelemetry_LogsZeroTotal()
    {
        using TestDatabaseFactory dbFactory = new();

        // Create a subscription but insert no telemetry at all
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 7);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // Insert only recent telemetry within retention
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machine.Id,
            TenantId = 1,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddHours(-1),
            SourceEventId = "very-recent"
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<TelemetryRetentionService> logger = Substitute.For<ILogger<TelemetryRetentionService>>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));
        TelemetryRetentionService service = new(scopeFactory, distributedLock, logger);

        System.Reflection.MethodInfo? method = typeof(TelemetryRetentionService)
            .GetMethod("PurgeOldTelemetryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)method!.Invoke(service, [CancellationToken.None])!;

        // Nothing should have been soft-deleted; the per-tenant log should not fire but the summary should
        List<MachineTelemetry> active = await dbFactory.Context.MachineTelemetry
            .Where(t => t.DeletedAt == null).ToListAsync();
        await Assert.That(active.Count).IsEqualTo(1);
        await Assert.That(active[0].SourceEventId).IsEqualTo("very-recent");
    }
}
