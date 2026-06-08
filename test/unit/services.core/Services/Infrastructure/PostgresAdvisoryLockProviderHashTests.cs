// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Test.Services.Infrastructure;

/// <summary>
/// M15 tests: pin the hash-stability and uniqueness contract for
/// <see cref="PostgresAdvisoryLockProvider.HashLockName"/>. SHA-256 truncated to 64 bits is
/// effectively collision-free at the current cardinality of lock-name domains
/// (see <see cref="LockNames"/>); future regressions that widen the domain to unbounded
/// cardinality must update this design.
/// </summary>
public sealed class PostgresAdvisoryLockProviderHashTests
{
    [Test]
    public async Task HashLockName_StableAcrossInvocations()
    {
        long first = PostgresAdvisoryLockProvider.HashLockName(LockNames.StateStreaming);
        long second = PostgresAdvisoryLockProvider.HashLockName(LockNames.StateStreaming);

        await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task HashLockName_DifferentInputs_ProduceDifferentKeys()
    {
        long a = PostgresAdvisoryLockProvider.HashLockName(LockNames.StateStreaming);
        long b = PostgresAdvisoryLockProvider.HashLockName(LockNames.UsageHeartbeat);

        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task HashLockName_FixedNamesAndTenantScopedSample_AllDistinct()
    {
        // Verify that every fixed lock name plus a representative cross-section of tenant-scoped
        // names hashes to a distinct 64-bit key. With ~2^32 birthday bound, a few hundred names
        // collide with probability vanishingly small — a collision here would indicate a real
        // implementation regression (e.g., wrong slice of the SHA-256 digest).
        HashSet<long> keys = new();
        foreach (string name in LockNames.FixedNames)
        {
            long key = PostgresAdvisoryLockProvider.HashLockName(name);
            await Assert.That(keys.Add(key)).IsTrue();
        }

        for (int tenantId = 1; tenantId <= 100; tenantId++)
        {
            string name = $"{LockNames.HealthSweepTenantPrefix}{tenantId}";
            long key = PostgresAdvisoryLockProvider.HashLockName(name);
            await Assert.That(keys.Add(key)).IsTrue();
        }
    }

    [Test]
    public async Task HashLockName_NullOrEmpty_Throws()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PostgresAdvisoryLockProvider.HashLockName(string.Empty);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }
}
