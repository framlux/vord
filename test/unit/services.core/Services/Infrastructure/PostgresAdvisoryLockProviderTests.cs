// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Test.Services.Infrastructure;

public sealed class PostgresAdvisoryLockProviderTests
{
    [Test]
    public async Task HashLockName_SameInput_ProducesSameHash()
    {
        // Intent: the advisory lock key must be deterministic across calls and replicas.
        // Without this property two replicas calling for the same lock name would compute
        // different keys and both think they own the lock.
        long first = PostgresAdvisoryLockProvider.HashLockName("tenant-7:health-sweep");
        long second = PostgresAdvisoryLockProvider.HashLockName("tenant-7:health-sweep");

        await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task HashLockName_DifferentInputs_ProduceDifferentHashes()
    {
        // Intent: collisions across distinct lock names would silently serialize unrelated jobs.
        // The full SHA-256 is collision-resistant; the truncated 8-byte projection inherits the
        // property for the cardinality the app produces (a few hundred lock names).
        long aHash = PostgresAdvisoryLockProvider.HashLockName("tenant-1:health-sweep");
        long bHash = PostgresAdvisoryLockProvider.HashLockName("tenant-2:health-sweep");
        long cHash = PostgresAdvisoryLockProvider.HashLockName("tenant-1:state-streaming");

        await Assert.That(aHash).IsNotEqualTo(bHash);
        await Assert.That(aHash).IsNotEqualTo(cHash);
        await Assert.That(bHash).IsNotEqualTo(cHash);
    }

    [Test]
    public async Task HashLockName_KnownInput_ProducesStableValue()
    {
        // Intent: pin the hash output so any change to the hashing strategy (algorithm change,
        // endianness flip, encoding swap) is caught immediately. A regression here would change
        // every lock key under a deploy, causing two versions of the app to NOT contend on the
        // same lock during a rolling restart.
        long actual = PostgresAdvisoryLockProvider.HashLockName("vord:test-key");

        // The expected value below is the first 8 little-endian bytes of SHA-256("vord:test-key").
        // Computed once and pinned; do not change without explicit migration consideration.
        await Assert.That(actual).IsEqualTo(-1747855882179900635L);
    }

    [Test]
    public async Task HashLockName_NullOrWhitespace_Throws()
    {
        // Intent: misuse should fail fast at the caller, not silently hash an empty string to a
        // single key that all "empty lock name" callers would contend on.
        await Assert.ThrowsAsync<ArgumentException>(() => Task.FromResult(PostgresAdvisoryLockProvider.HashLockName("")));
        await Assert.ThrowsAsync<ArgumentException>(() => Task.FromResult(PostgresAdvisoryLockProvider.HashLockName("   ")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Task.FromResult(PostgresAdvisoryLockProvider.HashLockName(null!)));
    }

    [Test]
    public async Task AdvisoryLockHandle_DisposalFlag_IsIntForInterlockedExchange()
    {
        // Intent: AdvisoryLockHandle.DisposeAsync must be safe under concurrent disposal as allowed
        // by IAsyncDisposable. The implementation uses Interlocked.Exchange on an int field; if a
        // future change reverts that to a plain bool, this test fires. Catches the exact regression
        // where two concurrent Dispose calls both pass the gate and double-commit the transaction.
        Type? handleType = typeof(PostgresAdvisoryLockProvider).GetNestedType(
            "AdvisoryLockHandle",
            BindingFlags.NonPublic);

        await Assert.That(handleType).IsNotNull();

        FieldInfo? disposedField = handleType!.GetField(
            "_disposed",
            BindingFlags.NonPublic | BindingFlags.Instance);

        await Assert.That(disposedField).IsNotNull();
        await Assert.That(disposedField!.FieldType).IsEqualTo(typeof(int));
    }

    [Test]
    public async Task AdvisoryLockHandle_DisposeAsync_SecondCallShortCircuits()
    {
        // Intent: once _disposed has been flipped to 1, a subsequent DisposeAsync must return
        // immediately without touching the underlying transaction/connection. We pre-set the flag
        // via reflection so the body's commit/dispose path is unreachable; if the gate fails the
        // null transaction reference would throw a NullReferenceException.
        Type handleType = typeof(PostgresAdvisoryLockProvider).GetNestedType(
            "AdvisoryLockHandle",
            BindingFlags.NonPublic)!;

        ConstructorInfo ctor = handleType.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)[0];
        object handle = ctor.Invoke([null!, null!, 0L, null!]);

        FieldInfo disposedField = handleType.GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance)!;
        disposedField.SetValue(handle, 1);

        IAsyncDisposable disposable = (IAsyncDisposable)handle;
        await disposable.DisposeAsync();
    }
}
