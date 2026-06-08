// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Framlux.FleetManagement.Test.Integration.Services.Infrastructure;

/// <summary>
/// Live integration tests for <see cref="PostgresAdvisoryLockProvider"/> against a real
/// Postgres backend (Testcontainers). The unit tests cover only the <see cref="HashLockName"/>
/// helper and the disposal-flag thread-safety; these tests cover the distributed-lock
/// behavior the production code actually relies on at horizontal scale.
/// </summary>
public sealed class PostgresAdvisoryLockProviderLiveTests
{
    private static PostgresFixture _fixture = default!;

    /// <summary>
    /// Starts the Postgres container once per test class.
    /// </summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();
    }

    /// <summary>
    /// Tears down the container after all tests in the class run.
    /// </summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    private static PostgresAdvisoryLockProvider CreateProvider()
    {
        return new PostgresAdvisoryLockProvider(
            _fixture.DataSource,
            NullLogger<PostgresAdvisoryLockProvider>.Instance);
    }

    [Test]
    public async Task TryAcquireAsync_FirstCaller_ReturnsHandle()
    {
        // Intent: happy-path acquisition against a real Postgres backend.
        // If this fails the entire distributed-lock primitive is broken.
        PostgresAdvisoryLockProvider provider = CreateProvider();

        await using IAsyncDisposable? handle = await provider.TryAcquireAsync(
            $"vord:live:happy:{Guid.NewGuid():N}",
            CancellationToken.None);

        await Assert.That(handle).IsNotNull();
    }

    [Test]
    public async Task TryAcquireAsync_LockHeldByAnother_ReturnsNull()
    {
        // Intent: two concurrent acquirers on the same lock name must serialize — exactly one
        // gets the handle and the other gets null. This is the load-bearing property for every
        // job that uses advisory locking to coordinate across replicas.
        PostgresAdvisoryLockProvider providerA = CreateProvider();
        PostgresAdvisoryLockProvider providerB = CreateProvider();
        string lockName = $"vord:live:contend:{Guid.NewGuid():N}";

        await using IAsyncDisposable? handleA = await providerA.TryAcquireAsync(lockName, CancellationToken.None);
        IAsyncDisposable? handleB = await providerB.TryAcquireAsync(lockName, CancellationToken.None);

        await Assert.That(handleA).IsNotNull();
        await Assert.That(handleB).IsNull();
    }

    [Test]
    public async Task TryAcquireAsync_HandleDisposed_NextAcquireSucceeds()
    {
        // Intent: disposal must release the underlying advisory lock. If COMMIT-on-dispose
        // ever regressed to a no-op the lock would survive forever on this connection.
        PostgresAdvisoryLockProvider provider = CreateProvider();
        string lockName = $"vord:live:release:{Guid.NewGuid():N}";

        IAsyncDisposable? first = await provider.TryAcquireAsync(lockName, CancellationToken.None);
        await Assert.That(first).IsNotNull();
        await first!.DisposeAsync();

        await using IAsyncDisposable? second = await provider.TryAcquireAsync(lockName, CancellationToken.None);
        await Assert.That(second).IsNotNull();
    }

    [Test]
    public async Task TryAcquireAsync_DifferentLockNames_DoNotContend()
    {
        // Intent: distinct lock names must not serialize each other. If the hash collapsed
        // unrelated keys to the same Postgres advisory id, unrelated jobs would block each
        // other — silently and catastrophically under load.
        PostgresAdvisoryLockProvider provider = CreateProvider();
        string nameA = $"vord:live:distinct:a:{Guid.NewGuid():N}";
        string nameB = $"vord:live:distinct:b:{Guid.NewGuid():N}";

        await using IAsyncDisposable? handleA = await provider.TryAcquireAsync(nameA, CancellationToken.None);
        await using IAsyncDisposable? handleB = await provider.TryAcquireAsync(nameB, CancellationToken.None);

        await Assert.That(handleA).IsNotNull();
        await Assert.That(handleB).IsNotNull();
    }

    [Test]
    public async Task AdvisoryLockHandle_DisposeAsync_TwiceConcurrent_DoesNotThrow()
    {
        // Intent: IAsyncDisposable permits concurrent DisposeAsync calls. The handle uses
        // Interlocked.Exchange to gate the release path so the second caller is a no-op.
        // If the gate ever regresses to a plain bool the second call would try to commit
        // an already-disposed transaction and throw ObjectDisposedException.
        PostgresAdvisoryLockProvider provider = CreateProvider();
        string lockName = $"vord:live:double-dispose:{Guid.NewGuid():N}";

        IAsyncDisposable? handle = await provider.TryAcquireAsync(lockName, CancellationToken.None);
        await Assert.That(handle).IsNotNull();

        await Task.WhenAll(
            handle!.DisposeAsync().AsTask(),
            handle.DisposeAsync().AsTask());
    }

    [Test]
    public async Task TryAcquireAsync_ConnectionDies_LockReleasedAfterTermination()
    {
        // Intent: when a worker process dies (SIGKILL / OOM), the backend Postgres pid still
        // holds the advisory lock until the dead TCP connection is detected OR the backend
        // is terminated. Production relies on Keepalive=30 to detect dead workers in ~1min;
        // here we accelerate that by explicitly calling pg_terminate_backend on the lock
        // holder, then verify a new acquisition succeeds.
        //
        // This is the closest the suite gets to verifying the documented crash-recovery
        // contract on IAdvisoryLockProvider. The full keepalive timing is too slow to run
        // in CI (~1min minimum); we test the post-termination state instead.
        PostgresAdvisoryLockProvider providerA = CreateProvider();
        PostgresAdvisoryLockProvider providerB = CreateProvider();
        string lockName = $"vord:live:crash:{Guid.NewGuid():N}";

        IAsyncDisposable? handleA = await providerA.TryAcquireAsync(lockName, CancellationToken.None);
        await Assert.That(handleA).IsNotNull();

        bool terminated = await TerminateBackendHoldingLock(lockName);
        await Assert.That(terminated).IsTrue();

        // Give Postgres a moment to release the lock on the terminated backend.
        await Task.Delay(TimeSpan.FromSeconds(2));

        await using IAsyncDisposable? handleB = await providerB.TryAcquireAsync(lockName, CancellationToken.None);
        await Assert.That(handleB).IsNotNull();
    }

    /// <summary>
    /// Looks up the backend pid holding the advisory lock for the given lock name and
    /// terminates it via <c>pg_terminate_backend</c>. Returns true if a pid was found and
    /// <c>pg_terminate_backend</c> returned true. Uses a separate connection so the running
    /// test process is not the lock holder being killed.
    /// Postgres represents a 64-bit advisory key as two 32-bit columns in pg_locks
    /// (<c>classid</c> high 32 bits, <c>objid</c> low 32 bits), so we reconstruct the key
    /// before comparing.
    /// </summary>
    private static async Task<bool> TerminateBackendHoldingLock(string lockName)
    {
        long key = PostgresAdvisoryLockProvider.HashLockName(lockName);
        int classId = (int)(key >> 32);
        int objId = unchecked((int)(key & 0xFFFFFFFFL));

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT pg_terminate_backend(pid)
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND classid = @classid
              AND objid = @objid
              AND granted = true
            LIMIT 1";
        cmd.Parameters.AddWithValue("classid", classId);
        cmd.Parameters.AddWithValue("objid", objId);

        object? result = await cmd.ExecuteScalarAsync();

        return (result is bool b) && b;
    }
}
