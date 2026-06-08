// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Hangfire;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Machines;

public sealed class HealthSweepTenantJobTests
{
    private static ServerConfigurationService CreateConfigService(int onlineThresholdSeconds = 300)
    {
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        cache.GetSettingAsync(Arg.Any<ServerConfigurationSettingKeys>(), Arg.Any<CancellationToken>())
            .Returns(onlineThresholdSeconds.ToString());

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        return new ServerConfigurationService(cache, redis);
    }

    private static IAdvisoryLockProvider AcquiringLockProvider()
    {
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        IAsyncDisposable handle = Substitute.For<IAsyncDisposable>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(handle);

        return provider;
    }

    private static IAdvisoryLockProvider BlockingLockProvider()
    {
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IAsyncDisposable?)null);

        return provider;
    }

    private static ISqlDialect SweepDialect(string sql = "/* test sweep sql */")
    {
        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.HealthSweepForTenant.Returns(sql);

        return dialect;
    }

    [Test]
    public async Task RunAsync_LockAcquired_RunsSweepWithDialectSqlAndThresholdSeconds()
    {
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.SweepHealthStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(7);

        HealthSweepTenantJob job = new(
            repo,
            SweepDialect("SELECT 1"),
            CreateConfigService(onlineThresholdSeconds: 120),
            AcquiringLockProvider(),
            Substitute.For<ILogger<HealthSweepTenantJob>>());

        await job.RunAsync(tenantId: 42, CancellationToken.None);

        await repo.Received(1).SweepHealthStatusAsync("SELECT 1", 42, 120, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_LockAcquired_DisposesLockHandleAfterWork()
    {
        // Intent: the disposable returned by TryAcquireAsync must be disposed exactly once
        // after the sweep work (or after the work throws) so the lock does not leak.
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        IAsyncDisposable handle = Substitute.For<IAsyncDisposable>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(handle);

        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.SweepHealthStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        HealthSweepTenantJob job = new(
            repo,
            SweepDialect(),
            CreateConfigService(),
            provider,
            Substitute.For<ILogger<HealthSweepTenantJob>>());

        await job.RunAsync(tenantId: 1, CancellationToken.None);

        await handle.Received(1).DisposeAsync();
    }

    [Test]
    public async Task RunAsync_LockNotAcquired_SkipsSweepWithoutThrowing()
    {
        // Intent: another replica holds the lock; this invocation must no-op and complete cleanly.
        // The recurring tick (every 15s) means missing one cycle is harmless.
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();

        HealthSweepTenantJob job = new(
            repo,
            SweepDialect(),
            CreateConfigService(),
            BlockingLockProvider(),
            Substitute.For<ILogger<HealthSweepTenantJob>>());

        await job.RunAsync(tenantId: 1, CancellationToken.None);

        await repo.DidNotReceive().SweepHealthStatusAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_LockKeyIncludesTenantId()
    {
        // Intent: the lock key must vary per tenant so different tenants run concurrently.
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IAsyncDisposable?)null);

        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();

        HealthSweepTenantJob job = new(
            repo,
            SweepDialect(),
            CreateConfigService(),
            provider,
            Substitute.For<ILogger<HealthSweepTenantJob>>());

        await job.RunAsync(tenantId: 1234, CancellationToken.None);

        await provider.Received(1).TryAcquireAsync(
            Arg.Is<string>(k => k.Contains("1234")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_TenantIdZero_Throws()
    {
        HealthSweepTenantJob job = new(
            Substitute.For<IMachineStateRepository>(),
            SweepDialect(),
            CreateConfigService(),
            AcquiringLockProvider(),
            Substitute.For<ILogger<HealthSweepTenantJob>>());

        ArgumentOutOfRangeException? ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => job.RunAsync(tenantId: 0, CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("tenantId");
    }

    [Test]
    public async Task RunAsync_TenantIdNegative_Throws()
    {
        HealthSweepTenantJob job = new(
            Substitute.For<IMachineStateRepository>(),
            SweepDialect(),
            CreateConfigService(),
            AcquiringLockProvider(),
            Substitute.For<ILogger<HealthSweepTenantJob>>());

        ArgumentOutOfRangeException? ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => job.RunAsync(tenantId: -5, CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("tenantId");
    }

    [Test]
    public async Task RunAsync_SweepThrows_ExceptionPropagatesAndLockReleased()
    {
        // Intent: when the sweep itself fails, the exception must propagate so Hangfire records
        // failure (and applies the [AutomaticRetry] policy), AND the lock must still be released
        // via the disposable's finally/await-using semantics so the next replica isn't blocked.
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        IAsyncDisposable handle = Substitute.For<IAsyncDisposable>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(handle);

        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.SweepHealthStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        HealthSweepTenantJob job = new(
            repo,
            SweepDialect(),
            CreateConfigService(),
            provider,
            Substitute.For<ILogger<HealthSweepTenantJob>>());

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.RunAsync(tenantId: 1, CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.Message).IsEqualTo("DB down");
        await handle.Received(1).DisposeAsync();
    }

    [Test]
    public async Task RunAsync_LockProviderThrows_ExceptionPropagates()
    {
        // Intent: an infrastructure failure inside the lock provider (e.g., Postgres connection
        // refused) must surface to Hangfire so the run is recorded as failed.
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("postgres unavailable"));

        HealthSweepTenantJob job = new(
            Substitute.For<IMachineStateRepository>(),
            SweepDialect(),
            CreateConfigService(),
            provider,
            Substitute.For<ILogger<HealthSweepTenantJob>>());

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.RunAsync(tenantId: 1, CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.Message).IsEqualTo("postgres unavailable");
    }

    [Test]
    public async Task RunAsync_TokenForwardedToProviderAndRepository()
    {
        using CancellationTokenSource cts = new();

        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IAsyncDisposable>());

        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();

        HealthSweepTenantJob job = new(
            repo,
            SweepDialect(),
            CreateConfigService(),
            provider,
            Substitute.For<ILogger<HealthSweepTenantJob>>());

        await job.RunAsync(tenantId: 7, cts.Token);

        await provider.Received(1).TryAcquireAsync(Arg.Any<string>(), cts.Token);
        await repo.Received(1).SweepHealthStatusAsync(Arg.Any<string>(), 7, Arg.Any<int>(), cts.Token);
    }

    [Test]
    public async Task Constructor_NullRepository_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            HealthSweepTenantJob _ = new(
                null!,
                SweepDialect(),
                CreateConfigService(),
                AcquiringLockProvider(),
                Substitute.For<ILogger<HealthSweepTenantJob>>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("machineStateRepository");
    }

    [Test]
    public async Task Constructor_NullDialect_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            HealthSweepTenantJob _ = new(
                Substitute.For<IMachineStateRepository>(),
                null!,
                CreateConfigService(),
                AcquiringLockProvider(),
                Substitute.For<ILogger<HealthSweepTenantJob>>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("dialect");
    }

    [Test]
    public async Task Constructor_NullConfigService_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            HealthSweepTenantJob _ = new(
                Substitute.For<IMachineStateRepository>(),
                SweepDialect(),
                null!,
                AcquiringLockProvider(),
                Substitute.For<ILogger<HealthSweepTenantJob>>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("configService");
    }

    [Test]
    public async Task Constructor_NullLockProvider_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            HealthSweepTenantJob _ = new(
                Substitute.For<IMachineStateRepository>(),
                SweepDialect(),
                CreateConfigService(),
                null!,
                Substitute.For<ILogger<HealthSweepTenantJob>>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("advisoryLockProvider");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            HealthSweepTenantJob _ = new(
                Substitute.For<IMachineStateRepository>(),
                SweepDialect(),
                CreateConfigService(),
                AcquiringLockProvider(),
                null!);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    [Test]
    public async Task SweepTenant_ZeroMachinesUpdated_DoesNotLogDebugSweptCount()
    {
        // Intent: logging fidelity. When the sweep finds nothing to update (no machines changed
        // health state since the previous tick), the job must NOT emit a "swept X machines" Debug
        // line. The recurring coordinator enqueues this job per-tenant per-tick; emitting a
        // no-op log for every quiet tenant would flood production logs at Debug for tenants
        // whose fleets are healthy. The production code gates the log on rowsAffected > 0;
        // this test pins that gate so a future "always log" regression is caught.
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.SweepHealthStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        ILogger<HealthSweepTenantJob> logger = Substitute.For<ILogger<HealthSweepTenantJob>>();

        HealthSweepTenantJob job = new(
            repo,
            SweepDialect(),
            CreateConfigService(),
            AcquiringLockProvider(),
            logger);

        await job.RunAsync(tenantId: 1, CancellationToken.None);

        // Inspect ILogger.Log() invocations directly. The LogDebug extension compiles to
        // Log<TState>(LogLevel.Debug, ...) where TState is FormattedLogValues — NSubstitute
        // matchers on generics are awkward, so filter on the underlying Log call instead.
        IReadOnlyList<NSubstitute.Core.ICall> logCalls = logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .ToList();

        int debugCount = logCalls.Count(c => (LogLevel)c.GetArguments()[0]! == LogLevel.Debug);

        await Assert.That(debugCount).IsEqualTo(0);
    }

    [Test]
    public async Task SweepTenant_NonZeroMachinesUpdated_LogsDebugSweptCount()
    {
        // Intent: complement of the zero-rows test above. When work actually happened, the Debug
        // log must fire so operators can correlate sweep activity with health-state transitions.
        // Pinning both sides of the gate keeps the contract symmetric.
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.SweepHealthStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(5);

        ILogger<HealthSweepTenantJob> logger = Substitute.For<ILogger<HealthSweepTenantJob>>();

        HealthSweepTenantJob job = new(
            repo,
            SweepDialect(),
            CreateConfigService(),
            AcquiringLockProvider(),
            logger);

        await job.RunAsync(tenantId: 1, CancellationToken.None);

        IReadOnlyList<NSubstitute.Core.ICall> logCalls = logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .ToList();

        int debugCount = logCalls.Count(c => (LogLevel)c.GetArguments()[0]! == LogLevel.Debug);

        await Assert.That(debugCount).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_AutomaticRetryAttribute_IsZeroAttempts()
    {
        // Intent: the tenant job is re-enqueued by the coordinator twice per minute. Hangfire-
        // level retries on a failed tenant sweep would duplicate work that the next coordinator
        // tick already covers, with no benefit. Pin Attempts=0 so a regression to the previous
        // Attempts=2 value (wasted work + queue noise) is caught.
        MethodInfo method = typeof(HealthSweepTenantJob).GetMethod(nameof(HealthSweepTenantJob.RunAsync))!;
        AutomaticRetryAttribute attr = method.GetCustomAttribute<AutomaticRetryAttribute>()!;

        await Assert.That(attr).IsNotNull();
        await Assert.That(attr.Attempts).IsEqualTo(0);
    }
}
