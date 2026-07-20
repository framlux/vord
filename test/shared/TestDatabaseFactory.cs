// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner.Generators.SQLite;
using FluentMigrator.Runner;
using FluentMigrator;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Data;
using System.Reflection;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Factory for creating disposable in-memory SQLite database contexts for testing.
/// </summary>
public sealed class TestDatabaseFactory : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    /// <summary>
    /// Gets the DatabaseContext backed by the in-memory SQLite database.
    /// </summary>
    public DatabaseContext Context { get; }

    /// <summary>
    /// Creates a new test database with all migrations applied.
    /// </summary>
    public TestDatabaseFactory()
    {
        // In-memory SQLite connection; must stay open for the lifetime of the test
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        // Run FluentMigrator migrations on a temp file then copy schema to in-memory DB.
        // FluentMigrator manages its own connections, so we use a temp file approach.
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        ApplyMigrations(tempFile);

        // Read schema from temp file and apply to in-memory DB.
        using (SqliteConnection tempConn = new($"Data Source={tempFile};Mode=ReadOnly"))
        {
            tempConn.Open();
            using SqliteCommand cmd = tempConn.CreateCommand();
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY rowid";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string sql = reader.GetString(0);

                using SqliteCommand createCmd = _connection.CreateCommand();
                createCmd.CommandText = sql;
                try
                {
                    createCmd.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    // Some statements may conflict (e.g., VersionInfo table), skip them
                }
            }
        }

        // Disable FK enforcement for unit tests — test data setup frequently inserts
        // records without full FK reference chains (e.g., telemetry for non-existent machines).
        using (SqliteCommand fkCmd = _connection.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA foreign_keys = OFF";
            fkCmd.ExecuteNonQuery();
        }

        // Clean up temp file
        try { File.Delete(tempFile); } catch { /* best effort */ }

        // Create LinqToDB DataContext using the in-memory connection
        DataOptions<DatabaseContext> dataOptions = new(
            new DataOptions()
                .UseSQLite(SQLiteProvider.Microsoft)
                .UseConnection(SQLiteTools.GetDataProvider(SQLiteProvider.Microsoft), _connection, false));

        Context = new DatabaseContext(dataOptions);
    }

    /// <summary>
    /// Runs all FluentMigrator migrations against a SQLite database file. Exposed so tests can
    /// migrate a standalone file and inspect the migrated state — including seed data, which the
    /// factory's schema-only copy into the in-memory database does not carry over.
    /// </summary>
    /// <param name="databaseFilePath">Path of the SQLite database file to migrate.</param>
    public static void ApplyMigrations(string databaseFilePath)
    {
        ServiceCollection migrationServices = new();
        migrationServices.AddFluentMigratorCore()
                .ConfigureRunner(r =>
                {
                    r.AddSQLite()
                     .WithGlobalConnectionString($"Data Source={databaseFilePath}")
                     .ScanIn(typeof(MigrationVersion).Assembly);
                });

        using ServiceProvider migrationProvider = migrationServices.BuildServiceProvider();
        using IServiceScope migrationScope = migrationProvider.CreateScope();

        // FluentMigrator's SQLiteTypeMap does not include DateTimeOffset.
        // Patch the live type map instance via reflection before running migrations.
        // Path: SQLiteGenerator → GeneratorBase._column → ColumnBase._typeMap → SetTypeMap(DateTimeOffset, "TEXT")
        SQLiteGenerator generator = migrationScope.ServiceProvider.GetRequiredService<SQLiteGenerator>();
        generator.CompatibilityMode = CompatibilityMode.LOOSE;
        PatchTypeMap(generator);

        IMigrationRunner migrationRunner = migrationScope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        migrationRunner.MigrateUp();
    }

    /// <summary>
    /// Patches the SQLiteTypeMap on the generator to support DateTimeOffset.
    /// Reflection path (confirmed via diagnostic): GeneratorBase._column → ColumnBase._typeMap.
    /// Calls the protected TypeMapBase.SetTypeMap(DbType, string) to add DateTimeOffset → TEXT.
    /// </summary>
    private static void PatchTypeMap(SQLiteGenerator generator)
    {
        // Walk up the generator hierarchy to find _column
        object column = GetFieldValue(generator, "_column")
            ?? throw new InvalidOperationException("Could not find _column field on SQLiteGenerator hierarchy");

        // Walk up the column hierarchy to find _typeMap
        object typeMap = GetFieldValue(column, "_typeMap")
            ?? throw new InvalidOperationException("Could not find _typeMap field on column hierarchy");

        // Walk up the type map hierarchy to find Set(DbType, string)
        MethodInfo? setMethod = null;
        Type? setType = typeMap.GetType();
        while (setType != null && setMethod == null)
        {
            setMethod = setType.GetMethod("SetTypeMap", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(DbType), typeof(string)]);
            setType = setType.BaseType;
        }

        if (setMethod == null)
        {
            throw new InvalidOperationException("Could not find SetTypeMap(DbType, string) method on type map hierarchy");
        }

        setMethod.Invoke(typeMap, [DbType.DateTimeOffset, "TEXT"]);
    }

    /// <summary>
    /// Walks the type hierarchy of an object to find and return a private field value.
    /// </summary>
    private static object? GetFieldValue(object target, string fieldName)
    {
        Type? type = target.GetType();
        while (type != null)
        {
            FieldInfo? field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                return field.GetValue(target);
            }

            type = type.BaseType;
        }

        return null;
    }

    /// <summary>
    /// Disposes the in-memory SQLite connection.
    /// </summary>
    public void Dispose()
    {
        if (_disposed == false)
        {
            Context.Dispose();
            _connection.Close();
            _connection.Dispose();
            _disposed = true;
        }
    }
}
