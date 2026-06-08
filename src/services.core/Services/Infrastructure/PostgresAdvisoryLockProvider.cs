// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Npgsql;
using NpgsqlTypes;

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// PostgreSQL implementation of <see cref="IAdvisoryLockProvider"/> backed by
/// <c>pg_try_advisory_xact_lock</c>. The lock is transaction-scoped — the dedicated connection
/// stays open with an active transaction for the lifetime of the returned disposable. Disposal
/// commits the transaction, which Postgres releases as part of the commit contract. Using the
/// transaction-scoped variant (rather than session-scoped) sidesteps Npgsql connection-pool reset
/// semantics: even if the connection is returned to the pool, the lock has already been released
/// by the explicit COMMIT.
/// </summary>
public sealed class PostgresAdvisoryLockProvider : IAdvisoryLockProvider
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresAdvisoryLockProvider> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="PostgresAdvisoryLockProvider"/> class.
    /// </summary>
    /// <param name="dataSource">The Npgsql data source used to open dedicated connections.</param>
    /// <param name="logger">The logger.</param>
    public PostgresAdvisoryLockProvider(NpgsqlDataSource dataSource, ILogger<PostgresAdvisoryLockProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IAsyncDisposable?> TryAcquireAsync(string lockName, CancellationToken ct)
    {
        long key = HashLockName(lockName);
        NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(ct);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(ct);

            await using NpgsqlCommand cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT pg_try_advisory_xact_lock(@key)";
            cmd.Parameters.Add("key", NpgsqlDbType.Bigint).Value = key;

            object? result = await cmd.ExecuteScalarAsync(ct);
            bool acquired = (result is bool b) && b;

            if (acquired == false)
            {
                await transaction.RollbackAsync(ct);
                await transaction.DisposeAsync();
                await connection.DisposeAsync();

                return null;
            }

            return new AdvisoryLockHandle(connection, transaction, key, _logger);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Hashes a lock name into a stable 64-bit key for <c>pg_try_advisory_xact_lock(bigint)</c>.
    /// Uses SHA-256 truncated to the first eight bytes, read as little-endian — deterministic
    /// across replicas regardless of host endianness.
    /// </summary>
    /// <param name="lockName">The lock name to hash.</param>
    /// <returns>A stable 64-bit advisory-lock key.</returns>
    internal static long HashLockName(string lockName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(lockName));

        return BinaryPrimitives.ReadInt64LittleEndian(hash.AsSpan(0, 8));
    }

    private sealed class AdvisoryLockHandle : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;
        private readonly long _key;
        private readonly ILogger _logger;
        private int _disposed;

        public AdvisoryLockHandle(NpgsqlConnection connection, NpgsqlTransaction transaction, long key, ILogger logger)
        {
            _connection = connection;
            _transaction = transaction;
            _key = key;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            // IAsyncDisposable permits concurrent DisposeAsync calls; Interlocked.Exchange makes
            // the first caller perform the release and subsequent callers no-op.
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                // Committing the transaction releases the xact-scoped advisory lock per the
                // Postgres contract. Rolling back would also release it; we prefer commit so
                // any (future) side-effects in the same transaction durable-succeed.
                await _transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                // Disposing the connection releases any held locks regardless, so a commit
                // failure is non-fatal — but log it for visibility.
                _logger.LogWarning(ex, "Advisory lock transaction commit failed for key {Key}; connection close will release the lock", _key);
            }
            finally
            {
                await _transaction.DisposeAsync();
                await _connection.DisposeAsync();
            }
        }
    }
}
