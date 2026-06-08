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
    public Task<IAsyncDisposable?> TryAcquireAsync(string lockName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);

        return Task.FromResult<IAsyncDisposable?>(NoOpHandle.Instance);
    }

    private sealed class NoOpHandle : IAsyncDisposable
    {
        public static readonly NoOpHandle Instance = new();

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
