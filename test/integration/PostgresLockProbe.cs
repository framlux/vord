// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Npgsql;

namespace Framlux.FleetManagement.Test.Integration;

/// <summary>
/// Lets a live test interleave two connections deterministically: instead of sleeping and hoping the
/// second connection has reached the lock, it asks the server whether that connection is waiting on
/// one. Each connection under test carries its own application name so it can be told apart.
/// </summary>
public static class PostgresLockProbe
{
    /// <summary>
    /// Returns a copy of the connection string whose sessions identify themselves with the given
    /// application name.
    /// </summary>
    /// <param name="connectionString">The connection string to copy.</param>
    /// <param name="applicationName">The name the server reports for sessions opened with the copy.</param>
    public static string WithApplicationName(string connectionString, string applicationName)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString)
        {
            ApplicationName = applicationName,
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Waits until a session with the given application name is blocked on a lock, or until the work
    /// it was running has finished without ever blocking.
    /// </summary>
    /// <param name="connectionString">A connection string for the database the sessions use.</param>
    /// <param name="applicationName">The application name of the session expected to block.</param>
    /// <param name="work">The task driving that session.</param>
    /// <param name="cancellationToken">A failsafe that stops the wait when the test would otherwise hang.</param>
    /// <returns><c>true</c> when the session was seen blocked; <c>false</c> when the work completed first.</returns>
    public static async Task<bool> WaitUntilBlockedAsync(
        string connectionString, string applicationName, Task work, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection probe = new(connectionString);
        await probe.OpenAsync(cancellationToken);

        while (true)
        {
            await using NpgsqlCommand command = probe.CreateCommand();
            command.CommandText =
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name AND wait_event_type = 'Lock'";
            command.Parameters.AddWithValue("name", applicationName);
            long blocked = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            if (blocked > 0)
            {
                return true;
            }

            if (work.IsCompleted)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }
}
