// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Data;
using Framlux.FleetManagement.Database.Repositories;

namespace Framlux.FleetManagement.Test.Integration;

/// <summary>
/// Wraps a transaction provider so the commit of every transaction it begins stops at a gate. A test
/// uses it to hold a handler's transaction open, with every lock it has taken, at the exact moment
/// just before the commit, while a second connection is started against the same rows.
/// </summary>
public sealed class CommitGatedTransactionProvider : IDatabaseTransactionProvider
{
    private readonly IDatabaseTransactionProvider _inner;
    private readonly TaskCompletionSource _reachedCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Creates a gate in front of the given provider.
    /// </summary>
    /// <param name="inner">The provider whose transactions are gated.</param>
    public CommitGatedTransactionProvider(IDatabaseTransactionProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
    }

    /// <summary>
    /// Completes when a gated transaction has asked to commit and is waiting to be released.
    /// </summary>
    public Task ReachedCommit => _reachedCommit.Task;

    /// <summary>
    /// Lets every waiting commit, and every later one, proceed.
    /// </summary>
    public void Release()
    {
        _release.TrySetResult();
    }

    /// <inheritdoc/>
    public async Task<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        IDatabaseTransaction transaction = await _inner.BeginTransactionAsync(cancellationToken);

        return new GatedTransaction(transaction, _reachedCommit, _release.Task);
    }

    /// <inheritdoc/>
    public async Task<IDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        IDatabaseTransaction transaction = await _inner.BeginTransactionAsync(isolationLevel, cancellationToken);

        return new GatedTransaction(transaction, _reachedCommit, _release.Task);
    }

    /// <inheritdoc/>
    public bool IsSerializationConflict(Exception exception)
    {
        return _inner.IsSerializationConflict(exception);
    }

    private sealed class GatedTransaction : IDatabaseTransaction
    {
        private readonly IDatabaseTransaction _inner;
        private readonly TaskCompletionSource _reachedCommit;
        private readonly Task _released;

        public GatedTransaction(IDatabaseTransaction inner, TaskCompletionSource reachedCommit, Task released)
        {
            _inner = inner;
            _reachedCommit = reachedCommit;
            _released = released;
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            _reachedCommit.TrySetResult();
            await _released;
            await _inner.CommitAsync(cancellationToken);
        }

        public void Dispose()
        {
            _inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            return _inner.DisposeAsync();
        }
    }
}
