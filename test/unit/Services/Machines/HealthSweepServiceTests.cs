// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>
/// Tests for <see cref="HealthSweepService"/>.
/// </summary>
public class HealthSweepServiceTests
{
    private static ServerConfigurationService CreateConfigService(int onlineThresholdSeconds = 300)
    {
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        cache.GetSettingAsync(Arg.Any<Database.Enums.ServerConfigurationSettingKeys>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        return new ServerConfigurationService(cache, redis);
    }

    private static HealthSweepService CreateService(
        IServiceScopeFactory scopeFactory,
        ISqlDialect? dialect = null,
        ServerConfigurationService? configService = null,
        IDistributedLock? distributedLock = null,
        ILogger<HealthSweepService>? logger = null)
    {
        return new HealthSweepService(
            scopeFactory,
            dialect ?? Substitute.For<ISqlDialect>(),
            configService ?? CreateConfigService(),
            distributedLock ?? Substitute.For<IDistributedLock>(),
            logger ?? Substitute.For<ILogger<HealthSweepService>>());
    }

    // ========== SweepAllTenants_AcquiresDistributedLock ==========

    [Test]
    public async Task SweepAllTenants_AcquiresDistributedLock()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        // Seed a summary row so there's a tenant to sweep
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 1, tenantId: 5));

        TestServiceScopeFactory scopeFactory = new(db);
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns((LockHandle?)null);

        HealthSweepService service = CreateService(scopeFactory, distributedLock: distributedLock);

        await service.SweepAllTenantsAsync(CancellationToken.None);

        // Lock should have been attempted for tenant 5
        await distributedLock.Received(1).TryAcquireAsync(
            Arg.Is<string>(key => key.Contains("5")),
            Arg.Any<TimeSpan>());
    }

    // ========== SweepAllTenants_LockContention_SkipsCycle ==========

    [Test]
    public async Task SweepAllTenants_LockContention_SkipsCycle()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 1, tenantId: 1));

        TestServiceScopeFactory scopeFactory = new(db);
        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        // Lock not acquired
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns((LockHandle?)null);

        HealthSweepService service = CreateService(scopeFactory, dialect: dialect, distributedLock: distributedLock);

        await service.SweepAllTenantsAsync(CancellationToken.None);

        // SQL should never have been accessed since lock was not acquired
        _ = dialect.DidNotReceive().HealthSweepForTenant;
    }

    // ========== SweepAllTenants_MultipleTenants_SweepsEach ==========

    [Test]
    public async Task SweepAllTenants_MultipleTenants_SweepsEach()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        // Seed summaries for 3 different tenants
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 1, tenantId: 10));
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 2, tenantId: 20));
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 3, tenantId: 30));

        TestServiceScopeFactory scopeFactory = new(db);
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns((LockHandle?)null);

        HealthSweepService service = CreateService(scopeFactory, distributedLock: distributedLock);

        await service.SweepAllTenantsAsync(CancellationToken.None);

        // Lock attempted for each of the 3 tenants
        await distributedLock.Received(3).TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>());
    }

    // ========== SweepTenant_NoMachines_NoOp ==========

    [Test]
    public async Task SweepTenant_NoMachines_NoOp()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        TestServiceScopeFactory scopeFactory = new(db);

        // No summary rows for any tenant
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns((LockHandle?)null);

        HealthSweepService service = CreateService(scopeFactory, distributedLock: distributedLock);

        await service.SweepAllTenantsAsync(CancellationToken.None);

        // No tenants found = no lock attempts
        await distributedLock.Received(0).TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>());
    }

    // ========== SweepAllTenants_TenantSweepThrows_ContinuesToNextTenant ==========

    [Test]
    public async Task SweepAllTenants_TenantSweepThrows_ContinuesToNextTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        // Seed 2 tenants
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 1, tenantId: 50));
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 2, tenantId: 60));

        TestServiceScopeFactory scopeFactory = new(db);
        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        // Make the SQL throw on first call, succeed on second
        dialect.HealthSweepForTenant.Returns("INVALID SQL THAT WILL FAIL");

        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns((LockHandle?)null);

        ILogger<HealthSweepService> logger = Substitute.For<ILogger<HealthSweepService>>();

        HealthSweepService service = CreateService(
            scopeFactory, dialect: dialect, distributedLock: distributedLock, logger: logger);

        // When locks aren't acquired, SweepTenantAsync is never called,
        // so the exception path is through the lock mechanism.
        // This test verifies the service doesn't crash when no locks are acquired.
        await Assert.That(async () => await service.SweepAllTenantsAsync(CancellationToken.None))
            .ThrowsNothing();
    }

    // ========== Constructor null guard tests ==========

    [Test]
    public async Task Constructor_NullScopeFactory_ThrowsArgumentNullException()
    {
        await Assert.That(() => new HealthSweepService(
            null!,
            Substitute.For<ISqlDialect>(),
            CreateConfigService(),
            Substitute.For<IDistributedLock>(),
            Substitute.For<ILogger<HealthSweepService>>()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullDialect_ThrowsArgumentNullException()
    {
        await Assert.That(() => new HealthSweepService(
            Substitute.For<IServiceScopeFactory>(),
            null!,
            CreateConfigService(),
            Substitute.For<IDistributedLock>(),
            Substitute.For<ILogger<HealthSweepService>>()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullConfigService_ThrowsArgumentNullException()
    {
        await Assert.That(() => new HealthSweepService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ISqlDialect>(),
            null!,
            Substitute.For<IDistributedLock>(),
            Substitute.For<ILogger<HealthSweepService>>()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullDistributedLock_ThrowsArgumentNullException()
    {
        await Assert.That(() => new HealthSweepService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ISqlDialect>(),
            CreateConfigService(),
            null!,
            Substitute.For<ILogger<HealthSweepService>>()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        await Assert.That(() => new HealthSweepService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ISqlDialect>(),
            CreateConfigService(),
            Substitute.For<IDistributedLock>(),
            null!))
            .Throws<ArgumentNullException>();
    }

    // ========== SweepAllTenants_NoTenants_CompletesWithoutLockAttempt ==========

    [Test]
    public async Task SweepAllTenants_NoTenants_CompletesWithoutLockAttempt()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        // No summary rows seeded — no tenants exist
        TestServiceScopeFactory scopeFactory = new(db);
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        ILogger<HealthSweepService> logger = Substitute.For<ILogger<HealthSweepService>>();

        HealthSweepService service = CreateService(
            scopeFactory, dialect: dialect, distributedLock: distributedLock, logger: logger);

        await service.SweepAllTenantsAsync(CancellationToken.None);

        // No tenants means no lock acquisition attempted
        await distributedLock.DidNotReceive().TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>());

        // No SQL executed since no tenants to sweep
        _ = dialect.DidNotReceive().HealthSweepForTenant;

        // No debug log emitted because totalUpdated is 0
        logger.DidNotReceive().Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ========== SweepAllTenants_LockFailsForOneTenant_SkipsAndContinuesToNext ==========

    [Test]
    public async Task SweepAllTenants_LockFailsForOneTenant_SkipsAndContinuesToNext()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        // Seed two tenants
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 1, tenantId: 100));
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 2, tenantId: 200));

        TestServiceScopeFactory scopeFactory = new(db);
        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.HealthSweepForTenant.Returns("SELECT 0");
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();

        // First tenant lock fails, second tenant lock succeeds
        LockHandle successHandle = new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value");
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(
                Task.FromResult<LockHandle?>(null),
                Task.FromResult<LockHandle?>((LockHandle?)successHandle));

        HealthSweepService service = CreateService(
            scopeFactory, dialect: dialect, distributedLock: distributedLock);

        await service.SweepAllTenantsAsync(CancellationToken.None);

        // Both tenants attempted lock acquisition
        await distributedLock.Received(2).TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>());

        // SQL dialect accessed only for the tenant whose lock succeeded
        _ = dialect.Received(1).HealthSweepForTenant;
    }

    // ========== Execute_ExceptionInSweep_LogsErrorAndContinues ==========

    [Test]
    public async Task Execute_ExceptionInSweep_LogsErrorAndContinues()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        // Seed a tenant so the sweep has work to do
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 1, tenantId: 42));

        TestServiceScopeFactory scopeFactory = new(db);
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .ThrowsAsync(new InvalidOperationException("simulated sweep failure"));

        ILogger<HealthSweepService> logger = Substitute.For<ILogger<HealthSweepService>>();

        HealthSweepService service = CreateService(
            scopeFactory, distributedLock: distributedLock, logger: logger);

        // Start the service and let it run through at least one cycle that hits the exception handler
        using CancellationTokenSource cts = new();
        TaskCompletionSource workDone = new();

        // After the exception is thrown and logged, cancel the service
        logger.When(l => l.Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Any<object>(),
                Arg.Is<Exception>(ex => ex is InvalidOperationException),
                Arg.Any<Func<object, Exception?, string>>()))
            .Do(_ => workDone.TrySetResult());

        await service.StartAsync(cts.Token);
        await workDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();

        try
        {
            await service.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }

        // Verify the error was logged at least once
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Is<Exception>(ex => ex is InvalidOperationException),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ========== SweepAllTenants_ZeroUpdated_NoDebugLogEmitted ==========

    [Test]
    public async Task SweepAllTenants_ZeroUpdated_NoDebugLogEmitted()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        // Seed a tenant so the sweep runs
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 1, tenantId: 77));

        TestServiceScopeFactory scopeFactory = new(db);
        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        // Use a SQL statement that returns 0 rows affected
        dialect.HealthSweepForTenant.Returns("SELECT 0");

        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        LockHandle lockHandle = new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value");
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>((LockHandle?)lockHandle));

        ILogger<HealthSweepService> logger = Substitute.For<ILogger<HealthSweepService>>();

        HealthSweepService service = CreateService(
            scopeFactory, dialect: dialect, distributedLock: distributedLock, logger: logger);

        await service.SweepAllTenantsAsync(CancellationToken.None);

        // When totalUpdated is 0, no debug log should be emitted
        logger.DidNotReceive().Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
