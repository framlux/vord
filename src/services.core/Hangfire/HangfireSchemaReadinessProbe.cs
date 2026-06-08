// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Npgsql;

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// Polls Postgres for the presence of Hangfire's schema-managed tables before the worker is
/// allowed to register an <c>IHostedService</c>-style Hangfire server. Belt-and-braces against
/// the worker pod starting before <c>migration-runner</c> has completed Hangfire's own DDL.
/// </summary>
public static class HangfireSchemaReadinessProbe
{
    /// <summary>
    /// Polls <c>SELECT 1 FROM hangfire.job LIMIT 0</c> until it succeeds or the timeout elapses.
    /// Returns silently on success; throws <see cref="TimeoutException"/> if the schema is not
    /// ready within <paramref name="timeout"/>. Errors other than schema-missing are surfaced
    /// immediately (we are not trying to absorb DB outages — only schema-ordering races).
    /// </summary>
    /// <param name="connectionString">The Postgres connection string.</param>
    /// <param name="timeout">Maximum total wait time.</param>
    /// <param name="pollInterval">Polling cadence. Defaults to 2 seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WaitForHangfireSchemaAsync(
        string connectionString,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        TimeSpan interval = pollInterval ?? TimeSpan.FromSeconds(2);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using NpgsqlConnection connection = new(connectionString);
                await connection.OpenAsync(ct);
                await using NpgsqlCommand cmd = new(@"SELECT 1 FROM ""hangfire"".""job"" LIMIT 0", connection);
                await cmd.ExecuteScalarAsync(ct);

                return;
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "42P01" || pgEx.SqlState == "3F000")
            {
                // 42P01 = undefined_table, 3F000 = invalid_schema_name. Both mean migration-runner
                // has not yet finished. Sleep and retry.
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for Hangfire schema (hangfire.job) to be ready. "
                    + "Verify that the migration-runner has completed before the worker starts.");
            }

            await Task.Delay(interval, ct);
        }
    }
}
