// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="RetryHelper"/>.
/// </summary>
public sealed class RetryHelperTests
{
    [Test]
    public async Task ExecuteWithRetryAsync_SucceedsOnFirstAttempt_ExecutesOnce()
    {
        int callCount = 0;

        await RetryHelper.ExecuteWithRetryAsync(
            async () => { callCount++; await Task.CompletedTask; },
            maxRetries: 3,
            baseDelayMs: 1);

        await Assert.That(callCount).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteWithRetryAsync_FailsTwiceThenSucceeds_RetriesCorrectly()
    {
        int callCount = 0;

        await RetryHelper.ExecuteWithRetryAsync(
            async () =>
            {
                callCount++;
                if (callCount < 3)
                {
                    throw new InvalidOperationException("transient");
                }

                await Task.CompletedTask;
            },
            maxRetries: 3,
            baseDelayMs: 1);

        await Assert.That(callCount).IsEqualTo(3);
    }

    [Test]
    public async Task ExecuteWithRetryAsync_GenericOverload_ReturnsValueAfterRetry()
    {
        int callCount = 0;

        int result = await RetryHelper.ExecuteWithRetryAsync(
            async () =>
            {
                callCount++;
                if (callCount < 2)
                {
                    throw new InvalidOperationException("transient");
                }

                return await Task.FromResult(42);
            },
            maxRetries: 3,
            baseDelayMs: 1);

        await Assert.That(result).IsEqualTo(42);
        await Assert.That(callCount).IsEqualTo(2);
    }

    [Test]
    public async Task ExecuteWithRetryAsync_ExhaustsAllRetries_ThrowsFinalException()
    {
        InvalidOperationException exception = new("permanent failure");

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await RetryHelper.ExecuteWithRetryAsync(
                () => throw exception,
                maxRetries: 2,
                baseDelayMs: 1);
        });

        await Assert.That(thrown.Message).IsEqualTo("permanent failure");
    }

    [Test]
    public async Task ExecuteWithRetryAsync_OperationCanceledException_NeverRetried()
    {
        int callCount = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await RetryHelper.ExecuteWithRetryAsync(
                () =>
                {
                    callCount++;

                    throw new OperationCanceledException();
                },
                maxRetries: 3,
                baseDelayMs: 1);
        });

        await Assert.That(callCount).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteWithRetryAsync_CancelledToken_PropagatesOperationCanceledException()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await RetryHelper.ExecuteWithRetryAsync(
                () => throw new InvalidOperationException("fail"),
                maxRetries: 3,
                baseDelayMs: 1000,
                ct: cts.Token);
        });
    }

    [Test]
    public async Task ExecuteWithRetryAsync_GenericOverload_ExhaustsRetriesAndThrows()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await RetryHelper.ExecuteWithRetryAsync<int>(
                () => throw new InvalidOperationException("always fails"),
                maxRetries: 1,
                baseDelayMs: 1);
        });
    }

    [Test]
    public async Task ExecuteWithRetryAsync_MaxRetriesZero_ExactlyOneAttempt()
    {
        int callCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await RetryHelper.ExecuteWithRetryAsync(
                () =>
                {
                    callCount++;

                    throw new InvalidOperationException("fail");
                },
                maxRetries: 0,
                baseDelayMs: 1);
        });

        await Assert.That(callCount).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteWithRetryAsync_MaxRetriesOne_TwoTotalAttempts()
    {
        int callCount = 0;

        await RetryHelper.ExecuteWithRetryAsync(
            async () =>
            {
                callCount++;
                if (callCount < 2)
                {
                    throw new InvalidOperationException("transient");
                }

                await Task.CompletedTask;
            },
            maxRetries: 1,
            baseDelayMs: 1);

        await Assert.That(callCount).IsEqualTo(2);
    }

    [Test]
    public async Task ExecuteWithRetryAsync_BaseDelayZero_DoesNotThrow()
    {
        int callCount = 0;

        await RetryHelper.ExecuteWithRetryAsync(
            async () =>
            {
                callCount++;
                if (callCount < 2)
                {
                    throw new InvalidOperationException("transient");
                }

                await Task.CompletedTask;
            },
            maxRetries: 1,
            baseDelayMs: 0);

        await Assert.That(callCount).IsEqualTo(2);
    }

    [Test]
    public async Task ExecuteWithRetryAsync_NullLogger_DoesNotThrow()
    {
        int callCount = 0;

        await RetryHelper.ExecuteWithRetryAsync(
            async () =>
            {
                callCount++;
                if (callCount < 2)
                {
                    throw new InvalidOperationException("transient");
                }

                await Task.CompletedTask;
            },
            maxRetries: 1,
            baseDelayMs: 1,
            logger: null);

        await Assert.That(callCount).IsEqualTo(2);
    }

    [Test]
    public async Task ExecuteWithRetryAsync_LogsWarningOnEachRetry()
    {
        ILogger logger = Substitute.For<ILogger>();
        int callCount = 0;

        await RetryHelper.ExecuteWithRetryAsync(
            async () =>
            {
                callCount++;
                if (callCount < 3)
                {
                    throw new InvalidOperationException("transient");
                }

                await Task.CompletedTask;
            },
            maxRetries: 3,
            baseDelayMs: 1,
            logger: logger);

        // Two retries should produce two warning log calls
        logger.Received(2).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task ExecuteWithRetryAsync_NoLoggingOnFirstSuccessfulAttempt()
    {
        ILogger logger = Substitute.For<ILogger>();

        await RetryHelper.ExecuteWithRetryAsync(
            () => Task.CompletedTask,
            maxRetries: 3,
            baseDelayMs: 1,
            logger: logger);

        logger.DidNotReceive().Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
