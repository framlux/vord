// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Test.Services.Infrastructure;

public sealed class NoOpAdvisoryLockProviderTests
{
    [Test]
    public async Task TryAcquireAsync_AlwaysReturnsAGrantedHandle()
    {
        // Intent: the no-op provider exists so SQLite-based test fixtures don't depend on a real
        // Postgres advisory lock. It must always grant the lock; otherwise jobs guarded by the
        // advisory lock would mysteriously no-op in tests.
        NoOpAdvisoryLockProvider provider = new();

        IAsyncDisposable? handle = await provider.TryAcquireAsync("any-key", CancellationToken.None);

        await Assert.That(handle).IsNotNull();

        // Dispose must succeed and be safely idempotent for the await-using callers in jobs.
        await handle!.DisposeAsync();
        await handle.DisposeAsync();
    }

    [Test]
    public async Task TryAcquireAsync_NullOrWhitespaceLockName_Throws()
    {
        // Intent: the no-op provider should still reject obviously malformed input so the contract
        // matches the Postgres implementation; tests with a bug that passes "" surface immediately.
        NoOpAdvisoryLockProvider provider = new();

        await Assert.ThrowsAsync<ArgumentException>(() => provider.TryAcquireAsync("", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.TryAcquireAsync("   ", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.TryAcquireAsync(null!, CancellationToken.None));
    }
}
