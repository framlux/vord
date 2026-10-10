// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics;
using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Repositories;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Framlux.FleetManagement.Test.Integration.Migrations;

/// <summary>
/// Verifies the partial index that serves alert evaluation's once-a-minute question of which tenants
/// still have an unresolved event: it exists on the partitioned events table, is carried by the
/// daily partitions, and is used by the query the repository actually issues.
/// </summary>
public sealed class AlertEventsTenantIdOpenIndexTests
{
    private const string IndexName = "IX_AlertEvents_TenantId_Open";

    private static PostgresFixture _fixture = default!;

    /// <summary>Starts the Postgres container once and runs migrations for all tests in this class.</summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(_fixture.ConnectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Warning));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    /// <summary>Stops the Postgres container after all tests in the class.</summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task Index_CoversTenantForUnresolvedEventsOnly()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'AlertEvents' AND indexname = @name",
            connection);
        command.Parameters.AddWithValue("name", IndexName);

        string? definition = await command.ExecuteScalarAsync() as string;

        await Assert.That(definition).IsNotNull();
        await Assert.That(definition!).Contains("(\"TenantId\")");
        await Assert.That(definition!).Contains("WHERE (\"Status\" <> 3)");

        // The migration writes the stored integer rather than the enum, because a shipped migration
        // must not change meaning if the enum is edited. Renumbering Resolved would desynchronise the
        // index from the query, and this is where that shows up.
        short resolved = (short)AlertEventStatus.Resolved;
        await Assert.That(definition!).Contains($"<> {resolved})");
    }

    [Test]
    public async Task Index_IsBuiltOnEveryDailyPartition()
    {
        // A partitioned parent's index is only useful if each partition carries a child of it; a
        // parent-only index would leave the per-partition scans sequential.
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT
                (SELECT count(*) FROM pg_inherits WHERE inhparent = 'public."AlertEvents"'::regclass),
                (SELECT count(*) FROM pg_inherits WHERE inhparent = ('public."' || @name || '"')::regclass)
            """,
            connection);
        command.Parameters.AddWithValue("name", IndexName);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        long partitions = reader.GetInt64(0);
        long indexedPartitions = reader.GetInt64(1);

        await Assert.That(partitions).IsGreaterThan(0L);
        await Assert.That(indexedPartitions).IsEqualTo(partitions);
    }

    [Test]
    public async Task OpenTenantsQuery_WhenFewEventsAreOpen_UsesThePartialIndexOnThePartitionHoldingThem()
    {
        // Intent: the repository's own statement, not a hand-written one, must be provable against the
        // partial index. Forty thousand events with one open in two thousand sit in a single daily
        // partition; without the index the planner has nothing better than a sequential scan of them.
        // The other partitions are empty, and the planner reads an empty table sequentially at zero
        // cost, so only the loaded partition is asserted to read an index.
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        // The foreign key to Tenants is not what is under test, so it is bypassed for the bulk load.
        await Execute(connection, "SET session_replication_role = replica");
        await Execute(connection, """
            INSERT INTO "AlertEvents" ("AlertRuleId", "TenantId", "MachineId", "Severity", "Message", "Status", "TriggeredAt")
            SELECT 1, (g % 20) + 1, 1, 1, 'event', CASE WHEN g % 2000 = 0 THEN 1 ELSE 3 END, now()
            FROM generate_series(1, 40000) AS g
            """);
        await Execute(connection, """ANALYZE "AlertEvents" """);

        string statement = await CaptureRepositoryQueryAsync();
        List<string> plan = await ExplainAsync(connection, statement);

        await Assert.That(plan.Any(line => line.Contains("Index Scan on") && line.Contains("_TenantId_idx"))).IsTrue();
    }

    private static async Task<string> CaptureRepositoryQueryAsync()
    {
        string captured = string.Empty;
        DataOptions<DatabaseContext> options = new(new DataOptions()
            .UsePostgreSQL(_fixture.ConnectionString)
            .UseTracing(TraceLevel.Info, info =>
            {
                if (info.TraceInfoStep == TraceInfoStep.BeforeExecute)
                {
                    captured = info.SqlText ?? string.Empty;
                }
            }));
        await using DatabaseContext db = new(options);
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);

        await repo.GetTenantIdsWithOpenAlertEventsAsync();

        return captured;
    }

    private static async Task<List<string>> ExplainAsync(NpgsqlConnection connection, string commandText)
    {
        List<string> lines = [];
        await using NpgsqlCommand command = new($"EXPLAIN {commandText}", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
