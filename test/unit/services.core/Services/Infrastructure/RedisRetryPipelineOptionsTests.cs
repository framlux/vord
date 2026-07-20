// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Polly;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="RedisRetryPipelineOptions"/> — the retry options factory shared by
/// the "redis-ping" resilience pipeline registration and its consumers' unit tests.
/// </summary>
public class RedisRetryPipelineOptionsTests
{
    [Test]
    public async Task Create_TransientFailure_RetriesAndLogsOperationNameFromContext()
    {
        ILogger logger = Substitute.For<ILogger>();
        ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .AddRetry(RedisRetryPipelineOptions.Create(TimeSpan.FromMilliseconds(1), logger))
            .Build();

        ResilienceContext context = ResilienceContextPool.Shared.Get("TestOperation", CancellationToken.None);
        int callCount = 0;
        try
        {
            await pipeline.ExecuteAsync(
                _ =>
                {
                    callCount++;
                    if (callCount < 3)
                    {
                        throw new TimeoutException("transient redis timeout");
                    }

                    return default;
                },
                context);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }

        await Assert.That(callCount).IsEqualTo(3);

        // Two retries should have occurred (callCount 1 and 2 both failed), each logging a
        // warning naming the operation carried on the ResilienceContext.
        logger.Received(2).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("TestOperation")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task Create_OperationCanceledException_NeverRetried()
    {
        ILogger logger = Substitute.For<ILogger>();
        ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .AddRetry(RedisRetryPipelineOptions.Create(TimeSpan.FromMilliseconds(1), logger))
            .Build();

        int callCount = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await pipeline.ExecuteAsync(_ =>
            {
                callCount++;

                throw new OperationCanceledException();
            });
        });

        await Assert.That(callCount).IsEqualTo(1);
    }

    [Test]
    public async Task Create_PermanentFailure_ExhaustsRetriesThenThrows()
    {
        ILogger logger = Substitute.For<ILogger>();
        ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .AddRetry(RedisRetryPipelineOptions.Create(TimeSpan.FromMilliseconds(1), logger))
            .Build();

        int callCount = 0;

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await pipeline.ExecuteAsync(_ =>
            {
                callCount++;

                throw new TimeoutException("permanent redis timeout");
            });
        });

        // Three retry attempts configured means one initial call plus three retries.
        await Assert.That(callCount).IsEqualTo(4);
    }

    [Test]
    public async Task Create_NullLogger_ThrowsArgumentNullException()
    {
        ArgumentNullException? thrown = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Task.FromResult(RedisRetryPipelineOptions.Create(TimeSpan.FromMilliseconds(1), null!)));

        await Assert.That(thrown).IsNotNull();
    }
}
