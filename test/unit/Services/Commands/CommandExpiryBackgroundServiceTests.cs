// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Commands;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="CommandExpiryBackgroundService"/>.
/// </summary>
public sealed class CommandExpiryBackgroundServiceTests
{
    [Test]
    public async Task ExecuteAsync_CallsExpirePendingCommands()
    {
        using CancellationTokenSource cts = new();
        TaskCompletionSource workDone = new();

        IRemoteCommandRepository remoteCommandRepository = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepository.ExpirePendingCommandsAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return Task.CompletedTask;
            });

        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IRemoteCommandRepository)).Returns(remoteCommandRepository);
        scope.ServiceProvider.Returns(serviceProvider);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));

        ILogger<CommandExpiryBackgroundService> logger = Substitute.For<ILogger<CommandExpiryBackgroundService>>();

        CommandExpiryBackgroundService service = new(scopeFactory, distributedLock, logger);

        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await remoteCommandRepository.Received(1).ExpirePendingCommandsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ExceptionDoesNotCrash()
    {
        using CancellationTokenSource cts = new();
        TaskCompletionSource workDone = new();

        IRemoteCommandRepository remoteCommandRepository = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepository.ExpirePendingCommandsAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                throw new InvalidOperationException("DB down");
            });

        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IRemoteCommandRepository)).Returns(remoteCommandRepository);
        scope.ServiceProvider.Returns(serviceProvider);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));

        ILogger<CommandExpiryBackgroundService> logger = Substitute.For<ILogger<CommandExpiryBackgroundService>>();

        CommandExpiryBackgroundService service = new(scopeFactory, distributedLock, logger);

        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // Verify the service logged the error rather than crashing.
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ========== Constructor null guard validation ==========

    [Test]
    public async Task Constructor_NullScopeFactory_Throws()
    {
        // Intent: Supplying a null scope factory must fail immediately at construction
        // so callers receive a clear ArgumentNullException rather than a deferred NullReferenceException.
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        ILogger<CommandExpiryBackgroundService> logger = Substitute.For<ILogger<CommandExpiryBackgroundService>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            CommandExpiryBackgroundService _ = new(null!, distributedLock, logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("scopeFactory");
    }

    [Test]
    public async Task Constructor_NullDistributedLock_Throws()
    {
        // Intent: Supplying a null distributed lock must fail immediately at construction.
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        ILogger<CommandExpiryBackgroundService> logger = Substitute.For<ILogger<CommandExpiryBackgroundService>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            CommandExpiryBackgroundService _ = new(scopeFactory, null!, logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("distributedLock");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        // Intent: Supplying a null logger must fail immediately at construction.
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            CommandExpiryBackgroundService _ = new(scopeFactory, distributedLock, null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    [Test]
    public async Task ExecuteAsync_LockNotAcquired_SkipsCycleWithoutCallingExpiry()
    {
        // Intent: When another replica holds the distributed lock, this instance must
        // skip the expiry work entirely — it must not touch the database at all.
        using CancellationTokenSource cts = new();
        TaskCompletionSource lockAttempted = new();

        IRemoteCommandRepository remoteCommandRepository = Substitute.For<IRemoteCommandRepository>();

        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IRemoteCommandRepository)).Returns(remoteCommandRepository);
        scope.ServiceProvider.Returns(serviceProvider);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(callInfo =>
            {
                lockAttempted.TrySetResult();

                return Task.FromResult<LockHandle?>(null);
            });

        ILogger<CommandExpiryBackgroundService> logger = Substitute.For<ILogger<CommandExpiryBackgroundService>>();

        CommandExpiryBackgroundService service = new(scopeFactory, distributedLock, logger);

        await service.StartAsync(cts.Token);
        await lockAttempted.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // The expiry method must never be called when the lock could not be acquired.
        await remoteCommandRepository.DidNotReceive().ExpirePendingCommandsAsync(Arg.Any<CancellationToken>());
    }
}
