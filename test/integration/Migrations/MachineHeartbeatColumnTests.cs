// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framlux.FleetManagement.Test.Integration.Migrations;

/// <summary>
/// Live tests for the heartbeat column migration. The health sweep runs as SQL against Postgres and
/// cannot read the Redis ping key, so the heartbeat has to land on the same row the sweep reads;
/// these prove the column applies to a real database and that the shipped heartbeat interval moves
/// with it, without overwriting a value an operator deliberately chose.
/// </summary>
public sealed class MachineHeartbeatColumnTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    /// <summary>Starts the Postgres container once and runs migrations for all tests in this class.</summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _migratedConnectionString = _fixture.ConnectionString;
        await RunMigrationsAsync(_migratedConnectionString);
    }

    /// <summary>Stops the Postgres container after all tests in the class.</summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task Migration_AddsLastHeartbeatAtColumn()
    {
        await using NpgsqlConnection connection = new(_migratedConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(
            """
            SELECT data_type, is_nullable FROM information_schema.columns
            WHERE table_name = 'MachineStateSummary' AND column_name = 'LastHeartbeatAt'
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        await Assert.That(await reader.ReadAsync()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo("timestamp with time zone");
        await Assert.That(reader.GetString(1)).IsEqualTo("YES");
    }

    [Test]
    public async Task Migration_AddsTelemetryStaleColumn()
    {
        await using NpgsqlConnection connection = new(_migratedConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(
            """
            SELECT data_type, is_nullable FROM information_schema.columns
            WHERE table_name = 'MachineStateSummary' AND column_name = 'TelemetryStale'
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        await Assert.That(await reader.ReadAsync()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo("boolean");
        await Assert.That(reader.GetString(1)).IsEqualTo("NO");
    }

    [Test]
    public async Task Migration_ReseedsUntouchedHeartbeatSetting()
    {
        await using NpgsqlConnection connection = new(_migratedConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(
            """
            SELECT "Value" FROM "ConfigurationSettings" WHERE "Key" = 1
            """, connection);
        object? value = await command.ExecuteScalarAsync();

        await Assert.That((string?)value).IsEqualTo("120");
    }

    [Test]
    [DependsOn(nameof(Migration_ReseedsUntouchedHeartbeatSetting))]
    public async Task Migration_LeavesAnOperatorModifiedHeartbeatSettingAlone()
    {
        // A row an operator has saved carries Version > 1, because UpsertSettingAsync increments it
        // on every write. The reseed must key on that, or it silently overrides a deliberate choice.
        //
        // A fresh database always has Version 1, so the scenario only exists on an already-migrated
        // database: put the row into the operator-modified state, replay the shipped statement, and
        // assert it did nothing. The statement text is the migration's own, not a copy.
        await using NpgsqlConnection connection = new(_migratedConnectionString);
        await connection.OpenAsync();

        await using (NpgsqlCommand modify = new(
            """
            UPDATE "ConfigurationSettings"
            SET "Value" = '300', "Version" = 4 WHERE "Key" = 1
            """, connection))
        {
            await modify.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand replay = new(AddMachineHeartbeatColumn.ReseedHeartbeatSql, connection))
        {
            await replay.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand read = new(
            """SELECT "Value" FROM "ConfigurationSettings" WHERE "Key" = 1""", connection);

        await Assert.That((string?)await read.ExecuteScalarAsync()).IsEqualTo("300");
    }

    private static async Task RunMigrationsAsync(string connectionString)
    {
        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Warning));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }
}
