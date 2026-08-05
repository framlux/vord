// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Npgsql;
using Testcontainers.PostgreSql;

namespace Framlux.FleetManagement.Test.Integration;

/// <summary>
/// Provides an isolated Postgres database with a configured <see cref="NpgsqlDataSource"/>.
/// All fixture instances share a single Postgres container for the whole test session and each
/// instance creates its own database on it, so classes stay isolated from one another without
/// paying for a container per class. Sixteen containers racing to publish a host port is a
/// documented source of intermittent startup failures under Podman.
/// Connection strings carry the same Keepalive settings as production
/// (see ServiceCollectionExtensions.AddRepositories) so crash-recovery semantics
/// match what the running server sees.
/// </summary>
public sealed class PostgresFixture : IAsyncDisposable
{
    // Sizing for the shared connection budget. The server ceiling is what keeps the suite off
    // "sorry, too many clients already": every class now draws on one budget instead of a private
    // container's default of 100, and a full run peaks around ninety backends, which left almost
    // no headroom at that default.
    //
    // The per-pool cap does no work at today's load — measured demand is under three connections
    // per pool — and exists only to bound future growth. It is set well above the busiest class
    // rather than at its demand: PostgresAdvisoryLockProviderLiveTests runs six tests in parallel
    // against one pool needing ten connections between them, and one of those tests deliberately
    // never disposes its lock handle, so that slot stays checked out for the rest of the class.
    // A cap that merely matched the current demand would leave the class with negative margin and
    // start queueing against the connection timeout the moment another lock test is added.
    //
    // Thirty-four pools live over a run — one per fixture plus one per per-test child database —
    // so the ceiling stays clear of what the caps could collectively demand.
    private const int MaxServerConnections = 800;
    private const int MaxConnectionsPerPool = 20;

    // ExecutionAndPublication guarantees the container factory runs exactly once even though TUnit
    // invokes InitializeAsync from many class-level hooks concurrently. The Lazy wraps the Task
    // rather than the container so callers await the same in-flight start instead of blocking a
    // thread on it.
    private static readonly Lazy<Task<PostgreSqlContainer>> SharedContainer =
        new(StartSharedContainerAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Data source backed by this fixture's database. Default until
    /// <see cref="InitializeAsync"/> completes.
    /// </summary>
    public NpgsqlDataSource DataSource { get; private set; } = default!;

    /// <summary>
    /// The full connection string for this fixture's database, including the password. Required
    /// for tests that need to issue their own CREATE DATABASE / DROP DATABASE statements or build
    /// per-test data sources — <see cref="NpgsqlDataSource.ConnectionString"/> strips the password
    /// by default, so reading from there alone is not enough. The container role is a superuser,
    /// so CREATE DATABASE issued over this connection works from any database.
    /// </summary>
    public string ConnectionString { get; private set; } = default!;

    /// <summary>
    /// Ensures the shared Postgres container is running, then creates a fresh database on it and
    /// constructs the data source that points at it.
    /// </summary>
    public async Task InitializeAsync()
    {
        PostgreSqlContainer container = await SharedContainer.Value;

        NpgsqlConnectionStringBuilder builder = new(container.GetConnectionString())
        {
            KeepAlive = 30,
            TcpKeepAlive = true,

            // Bounds every pool built from this string, including the child-database strings the
            // migration tests derive from it, so no single class can exhaust the shared budget.
            // Nothing in the suite drives more than a couple of concurrent connections at once.
            MaxPoolSize = MaxConnectionsPerPool,
        };

        // Guid "N" formatting is already lowercase hex, which keeps the identifier valid without
        // quoting games; the prefix keeps it from starting with a digit.
        string databaseName = $"vordtest_{Guid.NewGuid():N}";

        await using (NpgsqlConnection admin = new(builder.ConnectionString))
        {
            await admin.OpenAsync();
            await using NpgsqlCommand create = admin.CreateCommand();

            // CREATE DATABASE cannot run inside a transaction block, so it is issued bare.
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        builder.Database = databaseName;
        ConnectionString = builder.ConnectionString;
        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        // The database is deliberately left in place. DROP DATABASE fails while any backend is
        // still attached, and tests here intentionally leave terminated or pooled backends behind,
        // so dropping would reintroduce exactly the kind of intermittent teardown failure this
        // fixture exists to remove. The whole container is destroyed at the end of the session,
        // which reclaims every database with it.
    }

    /// <summary>
    /// Destroys the shared container once the test session ends. Ryuk is disabled for Podman
    /// (see CLAUDE.md), so nothing else will reap it.
    /// </summary>
    [After(TestSession)]
    public static async Task DisposeSharedContainerAsync()
    {
        if (SharedContainer.IsValueCreated == false)
        {
            return;
        }

        PostgreSqlContainer container;
        try
        {
            container = await SharedContainer.Value;
        }
        catch
        {
            // A failed start already disposed its own container and reported the failure through
            // every class that awaited it; rethrowing the cached fault here would only close the
            // session with a duplicate of that error.
            return;
        }

        await container.DisposeAsync();
    }

    private static async Task<PostgreSqlContainer> StartSharedContainerAsync()
    {
        PostgreSqlContainer container = new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")

            // Every class's pool now draws on one connection budget instead of a private
            // container's default of 100, and per-test child databases keep their own idle
            // physical connections for minutes. The raised ceiling sits comfortably above the
            // worst case that MaxPoolSize bounds the suite to.
            .WithCommand("-c", $"max_connections={MaxServerConnections}")
            .Build();

        try
        {
            await container.StartAsync();
        }
        catch
        {
            // Ryuk is disabled, so a container that was created but never started would leak
            // daemon-side once the faulted task is all that remains of it.
            await container.DisposeAsync();
            throw;
        }

        return container;
    }
}
