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

        PostgreSqlContainer container = await SharedContainer.Value;
        await container.DisposeAsync();
    }

    private static async Task<PostgreSqlContainer> StartSharedContainerAsync()
    {
        PostgreSqlContainer container = new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .Build();
        await container.StartAsync();

        return container;
    }
}
