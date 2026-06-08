// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Npgsql;
using Testcontainers.PostgreSql;

namespace Framlux.FleetManagement.Test.Integration;

/// <summary>
/// Provides a per-class Postgres container with a configured <see cref="NpgsqlDataSource"/>.
/// Connection string carries the same Keepalive settings as production
/// (see ServiceCollectionExtensions.AddRepositories) so crash-recovery semantics
/// match what the running server sees.
/// </summary>
public sealed class PostgresFixture : IAsyncDisposable
{
    private PostgreSqlContainer? _container;

    /// <summary>
    /// Data source backed by the running Postgres container. Default until
    /// <see cref="InitializeAsync"/> completes.
    /// </summary>
    public NpgsqlDataSource DataSource { get; private set; } = default!;

    /// <summary>
    /// The full container connection string including the password. Required for tests that
    /// need to issue their own CREATE DATABASE / DROP DATABASE statements or build per-test
    /// data sources — <see cref="NpgsqlDataSource.ConnectionString"/> strips the password by
    /// default, so reading from there alone is not enough.
    /// </summary>
    public string ConnectionString { get; private set; } = default!;

    /// <summary>
    /// Starts the Postgres container and constructs the data source.
    /// </summary>
    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .Build();
        await _container.StartAsync();

        NpgsqlConnectionStringBuilder builder = new(_container.GetConnectionString())
        {
            KeepAlive = 30,
            TcpKeepAlive = true,
        };
        ConnectionString = builder.ConnectionString;
        DataSource = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
