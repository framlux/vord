// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Lightweight retry helper with exponential backoff and jitter for transient failure handling.
/// Used for Redis and database operations that may fail transiently.
/// </summary>
public static class RetryHelper
{
    private static readonly Random Jitter = new();

    /// <summary>
    /// Executes an async action with retry on transient failures.
    /// Uses exponential backoff with jitter: base delay * 2^attempt + random jitter.
    /// </summary>
    /// <param name="action">The async action to execute.</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default 3).</param>
    /// <param name="baseDelayMs">Base delay in milliseconds (default 200).</param>
    /// <param name="logger">Optional logger for warning on retries.</param>
    /// <param name="operationName">Name of the operation for logging.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExecuteWithRetryAsync(
        Func<Task> action,
        int maxRetries = 3,
        int baseDelayMs = 200,
        ILogger? logger = null,
        string operationName = "operation",
        CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                await action();

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                int delayMs = (baseDelayMs * (1 << attempt)) + Jitter.Next(0, 100);
                logger?.LogWarning(ex, "Transient failure in {Operation}, retrying in {Delay}ms (attempt {Attempt}/{MaxRetries})",
                    operationName, delayMs, attempt + 1, maxRetries);
                await Task.Delay(delayMs, ct);
            }
        }
    }

    /// <summary>
    /// Executes an async function with retry on transient failures, returning a result.
    /// </summary>
    /// <typeparam name="T">The return type.</typeparam>
    /// <param name="func">The async function to execute.</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default 3).</param>
    /// <param name="baseDelayMs">Base delay in milliseconds (default 200).</param>
    /// <param name="logger">Optional logger for warning on retries.</param>
    /// <param name="operationName">Name of the operation for logging.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the function.</returns>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> func,
        int maxRetries = 3,
        int baseDelayMs = 200,
        ILogger? logger = null,
        string operationName = "operation",
        CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await func();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                int delayMs = (baseDelayMs * (1 << attempt)) + Jitter.Next(0, 100);
                logger?.LogWarning(ex, "Transient failure in {Operation}, retrying in {Delay}ms (attempt {Attempt}/{MaxRetries})",
                    operationName, delayMs, attempt + 1, maxRetries);
                await Task.Delay(delayMs, ct);
            }
        }

        // This should never be reached due to the throw in the catch block on the last attempt,
        // but the compiler needs it for type safety.
        throw new InvalidOperationException("Retry loop exhausted without success or exception.");
    }
}
