// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Test.Integration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Framlux.FleetManagement.Test.Integration.Services.Infrastructure;

/// <summary>
/// Live test proving the advisory-lock liveness check against real Postgres: after the lock's backend
/// session is terminated server-side, <see cref="IAdvisoryLock.IsAliveAsync"/> reports the lock lost and
/// the lock is available for a fresh acquisition. A substitute cannot reproduce a dropped Postgres
/// session, so this must run against a real engine.
/// </summary>
public sealed class AdvisoryLockLivenessLiveTests
{
    private static PostgresFixture _fixture = default!;

    /// <summary>Creates the class's own database on the shared Postgres container. No schema is needed for advisory locks.</summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();
    }

    /// <summary>Releases the class's data source after the class.</summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task IsAlive_AfterBackendTerminated_ReturnsFalse_AndLockBecomesAvailable()
    {
        PostgresAdvisoryLockProvider provider = new(_fixture.DataSource, NullLogger<PostgresAdvisoryLockProvider>.Instance);

        const string lockName = "liveness-test-lock";
        IAdvisoryLock? handle = await provider.TryAcquireAsync(lockName, CancellationToken.None);
        await Assert.That(handle).IsNotNull();
        await Assert.That(await handle!.IsAliveAsync(CancellationToken.None)).IsTrue();

        // A concurrent acquire of the same lock must be blocked while the first holder is alive.
        IAdvisoryLock? blocked = await provider.TryAcquireAsync(lockName, CancellationToken.None);
        await Assert.That(blocked).IsNull();

        // Terminate the backend that holds the advisory lock, from a separate admin connection.
        // pg_stat_activity and pg_locks span the whole cluster, so the sweep is confined to this
        // test's own database — other classes run their advisory-lock tests concurrently on the
        // same Postgres instance and must not have their lock holders killed from under them.
        await using (NpgsqlConnection admin = await _fixture.DataSource.OpenConnectionAsync(CancellationToken.None))
        {
            await using NpgsqlCommand kill = admin.CreateCommand();
            kill.CommandText = @"
                SELECT pg_terminate_backend(a.pid)
                FROM pg_stat_activity a
                JOIN pg_locks l ON l.pid = a.pid
                WHERE l.locktype = 'advisory'
                  AND a.pid <> pg_backend_pid()
                  AND a.datname = current_database()";
            await kill.ExecuteNonQueryAsync(CancellationToken.None);
        }

        // The liveness probe on the now-dead session must report the lock lost.
        await Assert.That(await handle.IsAliveAsync(CancellationToken.None)).IsFalse();

        // The terminated session released the lock, so a fresh acquisition succeeds.
        IAdvisoryLock? reacquired = await provider.TryAcquireAsync(lockName, CancellationToken.None);
        await Assert.That(reacquired).IsNotNull();

        await reacquired!.DisposeAsync();
        // Disposing a handle whose backend is already gone must not throw.
        await handle.DisposeAsync();
    }
}
