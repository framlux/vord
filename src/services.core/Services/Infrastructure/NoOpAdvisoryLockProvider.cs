// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// No-op advisory lock provider used in environments without PostgreSQL (notably the SQLite
/// functional-test fixture). Every <see cref="TryAcquireAsync"/> call succeeds immediately and
/// disposal is a no-op. Functional tests rely on the absence of multi-replica concurrency.
/// </summary>
public sealed class NoOpAdvisoryLockProvider : IAdvisoryLockProvider
{
    /// <inheritdoc/>
    public Task<IAdvisoryLock?> TryAcquireAsync(string lockName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);

        return Task.FromResult<IAdvisoryLock?>(NoOpHandle.Instance);
    }

    private sealed class NoOpHandle : IAdvisoryLock
    {
        public static readonly NoOpHandle Instance = new();

        // With no real lock there is nothing to lose; the single-holder assumption always holds.
        public Task<bool> IsAliveAsync(CancellationToken ct) => Task.FromResult(true);

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
