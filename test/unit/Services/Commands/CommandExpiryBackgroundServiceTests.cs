// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Server.Services.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

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

        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        cache.ExpirePendingCommandsAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return Task.CompletedTask;
            });

        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IDatabaseCache)).Returns(cache);
        scope.ServiceProvider.Returns(serviceProvider);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        ILogger<CommandExpiryBackgroundService> logger = Substitute.For<ILogger<CommandExpiryBackgroundService>>();

        CommandExpiryBackgroundService service = new(scopeFactory, logger);

        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await cache.Received(1).ExpirePendingCommandsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ExceptionDoesNotCrash()
    {
        using CancellationTokenSource cts = new();
        TaskCompletionSource workDone = new();

        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        cache.ExpirePendingCommandsAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                throw new InvalidOperationException("DB down");
            });

        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IDatabaseCache)).Returns(cache);
        scope.ServiceProvider.Returns(serviceProvider);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        ILogger<CommandExpiryBackgroundService> logger = Substitute.For<ILogger<CommandExpiryBackgroundService>>();

        CommandExpiryBackgroundService service = new(scopeFactory, logger);

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
}
