// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Commands;
using Hangfire;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services;

public sealed class RemoteCommandExpiryJobTests
{
    [Test]
    public async Task RunAsync_CallsExpirePendingCommands()
    {
        IRemoteCommandRepository repo = Substitute.For<IRemoteCommandRepository>();
        ILogger<RemoteCommandExpiryJob> logger = Substitute.For<ILogger<RemoteCommandExpiryJob>>();
        RemoteCommandExpiryJob job = new(repo, logger);

        await job.RunAsync(CancellationToken.None);

        await repo.Received(1).ExpirePendingCommandsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_RepositoryThrows_ExceptionPropagates()
    {
        // Intent: the job must NOT swallow repository exceptions — Hangfire's failed-job
        // tracking and retry behavior depends on exceptions surfacing to the runtime.
        IRemoteCommandRepository repo = Substitute.For<IRemoteCommandRepository>();
        repo.ExpirePendingCommandsAsync(Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("DB down"));
        ILogger<RemoteCommandExpiryJob> logger = Substitute.For<ILogger<RemoteCommandExpiryJob>>();
        RemoteCommandExpiryJob job = new(repo, logger);

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.RunAsync(CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.Message).IsEqualTo("DB down");
    }

    [Test]
    public async Task RunAsync_AlreadyCancelledToken_PassesTokenToRepository()
    {
        // Intent: the job must respect cancellation by forwarding the CancellationToken so the
        // repository can short-circuit. We don't assert it never runs — we assert the token flows through.
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        IRemoteCommandRepository repo = Substitute.For<IRemoteCommandRepository>();
        ILogger<RemoteCommandExpiryJob> logger = Substitute.For<ILogger<RemoteCommandExpiryJob>>();
        RemoteCommandExpiryJob job = new(repo, logger);

        await job.RunAsync(cts.Token);

        await repo.Received(1).ExpirePendingCommandsAsync(cts.Token);
    }

    [Test]
    public async Task Constructor_NullRepository_Throws()
    {
        ILogger<RemoteCommandExpiryJob> logger = Substitute.For<ILogger<RemoteCommandExpiryJob>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandExpiryJob _ = new(null!, logger);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("remoteCommandRepository");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        IRemoteCommandRepository repo = Substitute.For<IRemoteCommandRepository>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandExpiryJob _ = new(repo, null!);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    [Test]
    public async Task RunAsync_AutomaticRetryAttribute_IsZeroAttempts()
    {
        // Intent: pin Hangfire AutomaticRetry to 0 attempts. The default of 10 would cause
        // duplicate executions on transient failure; this job is not idempotent under retry.
        MethodInfo method = typeof(RemoteCommandExpiryJob).GetMethod(nameof(RemoteCommandExpiryJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        AutomaticRetryAttribute? attr = method.GetCustomAttribute<AutomaticRetryAttribute>();

        await Assert.That(attr).IsNotNull();
        await Assert.That(attr!.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_DisableConcurrentExecution_TimeoutMatchesContract()
    {
        // Intent: pin the lock timeout. Use CustomAttributeData since DisableConcurrentExecutionAttribute
        // does not expose timeout via a public property.
        MethodInfo method = typeof(RemoteCommandExpiryJob).GetMethod(nameof(RemoteCommandExpiryJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        CustomAttributeData? attrData = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(DisableConcurrentExecutionAttribute));

        await Assert.That(attrData).IsNotNull();
        await Assert.That(attrData!.ConstructorArguments.Count).IsEqualTo(1);
        int timeoutSeconds = (int)attrData.ConstructorArguments[0].Value!;
        await Assert.That(timeoutSeconds).IsEqualTo(90);
    }
}
