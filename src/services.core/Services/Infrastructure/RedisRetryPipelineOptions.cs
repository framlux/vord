// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Polly;
using Polly.Retry;

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Builds the retry strategy options for the "redis-ping" resilience pipeline. Shared by
/// the production registration in <c>ServiceCollectionExtensions.AddCoreInfrastructure</c>
/// and its unit tests, so a change to the retry semantics registered in production is
/// automatically exercised by the tests that pin them.
/// </summary>
public static class RedisRetryPipelineOptions
{
    /// <summary>
    /// The number of retry attempts applied by the pipeline, shared between the retry strategy's
    /// MaxRetryAttempts configuration and the {MaxRetries} value logged on each retry so the two
    /// can never drift apart.
    /// </summary>
    internal const int MaxAttempts = 3;

    /// <summary>
    /// Builds retry strategy options equivalent to the deleted RetryHelper: <see cref="MaxAttempts"/> retries,
    /// exponential backoff starting at <paramref name="baseDelay"/> with jitter, never
    /// retrying cancellation. Logs a warning naming the operation on each retry, using
    /// <see cref="ResilienceContext.OperationKey"/> so callers can attribute the retry to
    /// "RecordPing"/"SetAgentCapabilities" the way the deleted RetryHelper's
    /// operationName parameter did.
    /// </summary>
    /// <param name="baseDelay">The initial retry delay before exponential backoff.</param>
    /// <param name="logger">Logger used to warn on each retry attempt.</param>
    public static RetryStrategyOptions Create(TimeSpan baseDelay, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
            MaxRetryAttempts = MaxAttempts,
            Delay = baseDelay,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            OnRetry = args =>
            {
                string operationName = string.IsNullOrEmpty(args.Context.OperationKey)
                    ? "operation"
                    : args.Context.OperationKey;

                logger.LogWarning(
                    args.Outcome.Exception,
                    "Transient failure in {Operation}, retrying in {Delay}ms (attempt {Attempt}/{MaxRetries})",
                    operationName,
                    args.RetryDelay.TotalMilliseconds,
                    args.AttemptNumber + 1,
                    MaxAttempts);

                return default;
            },
        };
    }
}
