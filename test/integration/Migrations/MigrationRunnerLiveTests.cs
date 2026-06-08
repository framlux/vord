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
/// Live migration-runner integration tests. Runs the full FluentMigrator chain against a
/// Testcontainers Postgres so the new migrations introduced in the Hangfire refactor
/// (HangfireSchemaMigration, IntegrationDeliveryAttemptStatusMigration, AddCancelAtPeriodEnd,
/// AddDataExportFailureCount, IntegrationDeliveryAttemptStatusDefault) actually apply against a
/// real Postgres rather than only the in-memory SQLite used in unit tests.
/// </summary>
/// <remarks>
/// Per the saved-feedback memory <c>feedback_migrations_initial.md</c>: the app is deployed,
/// migrations must be idempotent, and a broken migration is unrecoverable in prod. These tests
/// catch that class of bug before it ships.
/// </remarks>
public sealed class MigrationRunnerLiveTests
{
    private static PostgresFixture _fixture = default!;

    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();
    }

    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    /// <summary>
    /// Builds a FluentMigrator runner wired to the supplied connection string, scanning the
    /// database project assembly for migrations. Mirrors the production runner config in
    /// <c>src/migrationRunner/Program.cs</c>.
    /// </summary>
    private static ServiceProvider BuildMigrationServices(string connectionString)
    {
        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Information));

        return services.BuildServiceProvider();
    }

    private static string BuildIsolatedDatabaseConnectionString()
    {
        // Derive a per-test database name from the master Postgres container so each test
        // runs against a fresh schema. The fixture's data source uses the default DB; we
        // create a new one for migration tests so re-runs don't observe each other's state.
        // The fixture's ConnectionString property carries the password (data-source's stripped form
        // doesn't); use it directly for both the admin connection and the new-db template.
        string baseConn = _fixture.ConnectionString;
        string dbName = $"migtest_{Guid.NewGuid():N}".Substring(0, 16).ToLowerInvariant();
        NpgsqlConnectionStringBuilder template = new(baseConn);

        // Issue CREATE DATABASE via the admin connection.
        using NpgsqlConnection admin = new(baseConn);
        admin.Open();
        using (NpgsqlCommand cmd = admin.CreateCommand())
        {
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            cmd.ExecuteNonQuery();
        }
        admin.Close();

        template.Database = dbName;

        return template.ConnectionString;
    }

    [Test]
    public async Task MigrationChain_AppliesCleanly_OnFreshDatabase()
    {
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        IMigrationRunner runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        runner.MigrateUp();

        // Verify a few canonical tables exist after the chain runs. The exact set lives in the
        // migration files; we sample tables from across the timeline to catch any partial-apply.
        await Assert.That(await TableExistsAsync(connStr, "Tenants")).IsTrue();
        await Assert.That(await TableExistsAsync(connStr, "Machines")).IsTrue();
        await Assert.That(await TableExistsAsync(connStr, "AlertConditionStates")).IsTrue();
        await Assert.That(await TableExistsAsync(connStr, "IntegrationDeliveryAttempts")).IsTrue();
        await Assert.That(await TableExistsAsync(connStr, "DataExportJobs")).IsTrue();
        // The hangfire schema is created by HangfireSchemaMigration; Hangfire's own tables
        // install separately at runtime, so we only assert the schema's presence here.
        await Assert.That(await SchemaExistsAsync(connStr, "hangfire")).IsTrue();
    }

    [Test]
    public async Task MigrationChain_IsIdempotent_OnSecondRun()
    {
        // Run the chain on a fresh DB, then run it again — FluentMigrator's VersionInfo table
        // tracks completed migrations and should make the second invocation a no-op (no errors,
        // no duplicate column adds).
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        IMigrationRunner runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        runner.MigrateUp();
        // Capture the version count after first run.
        long firstRunVersionCount = await CountVersionsAsync(connStr);

        // Second run — must not throw, must not add new VersionInfo rows.
        runner.MigrateUp();
        long secondRunVersionCount = await CountVersionsAsync(connStr);

        await Assert.That(secondRunVersionCount).IsEqualTo(firstRunVersionCount);
    }

    [Test]
    public async Task IntegrationDeliveryAttempts_StatusDefault_IsPending_AfterFullChain()
    {
        // The IntegrationDeliveryAttemptStatusDefaultMigration flips the Status column
        // default from 1 (Succeeded) to 0 (Pending). After running the full migration chain on a
        // fresh DB, the column default must be 0.
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        string? defaultValue = await GetColumnDefaultAsync(
            connStr, "IntegrationDeliveryAttempts", "Status");

        await Assert.That(defaultValue).IsNotNull();
        // Postgres returns the default expression as text (e.g. "0" or "1"). Normalize.
        await Assert.That(defaultValue!.Trim()).IsEqualTo("0");
    }

    [Test]
    public async Task DataExportJobs_FailureCount_AddedByMigration()
    {
        // H7's new AddDataExportFailureCountMigration adds FailureCount with default 0. Verify
        // the column exists and has a NOT NULL default of 0 after the chain.
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        await Assert.That(await ColumnExistsAsync(connStr, "DataExportJobs", "FailureCount")).IsTrue();
        string? defaultValue = await GetColumnDefaultAsync(connStr, "DataExportJobs", "FailureCount");
        await Assert.That(defaultValue).IsNotNull();
        await Assert.That(defaultValue!.Trim()).IsEqualTo("0");
    }

    [Test]
    public async Task HangfireSchema_CreatedByMigration()
    {
        // The consolidated/forwarded HangfireSchemaMigration creates the `hangfire` schema.
        // Hangfire's own DDL runs against this schema at server boot; the migration only ensures
        // the schema exists.
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        await Assert.That(await SchemaExistsAsync(connStr, "hangfire")).IsTrue();
    }

    // ----- helpers -----

    private static async Task<bool> TableExistsAsync(string connStr, string tableName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT EXISTS (
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = @t)";
        cmd.Parameters.AddWithValue("@t", tableName);
        object? result = await cmd.ExecuteScalarAsync();

        return (result is bool b) && b;
    }

    private static async Task<bool> SchemaExistsAsync(string connStr, string schemaName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT EXISTS (
            SELECT 1 FROM information_schema.schemata
            WHERE schema_name = @s)";
        cmd.Parameters.AddWithValue("@s", schemaName);
        object? result = await cmd.ExecuteScalarAsync();

        return (result is bool b) && b;
    }

    private static async Task<bool> ColumnExistsAsync(string connStr, string tableName, string columnName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @t AND column_name = @c)";
        cmd.Parameters.AddWithValue("@t", tableName);
        cmd.Parameters.AddWithValue("@c", columnName);
        object? result = await cmd.ExecuteScalarAsync();

        return (result is bool b) && b;
    }

    private static async Task<string?> GetColumnDefaultAsync(string connStr, string tableName, string columnName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT column_default
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @t AND column_name = @c";
        cmd.Parameters.AddWithValue("@t", tableName);
        cmd.Parameters.AddWithValue("@c", columnName);
        object? result = await cmd.ExecuteScalarAsync();
        if (result is null || result is DBNull)
        {
            return null;
        }

        return result.ToString();
    }

    private static async Task<long> CountVersionsAsync(string connStr)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM ""VersionInfo""";
        object? result = await cmd.ExecuteScalarAsync();

        return result is long l ? l : Convert.ToInt64(result);
    }
}
