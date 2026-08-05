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
/// Testcontainers Postgres so the consolidated migrations (InitialMigration and
/// InitialMigration2) — which fold in the hangfire schema, the integration-delivery lifecycle
/// columns and their Pending status default, the subscription cancel-at-period-end flag, and the
/// data-export failure count — actually apply against a real Postgres rather than only the
/// in-memory SQLite used in unit tests.
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
        // The hangfire schema is created by InitialMigration; Hangfire's own tables
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
        // InitialMigration creates the IntegrationDeliveryAttempts Status column with a default
        // of 0 (Pending). After running the full migration chain on a fresh DB, the column default
        // must be 0.
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
        // InitialMigration creates DataExportJobs with FailureCount defaulting to 0. Verify
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
        // InitialMigration creates the `hangfire` schema. Hangfire's own DDL runs against this
        // schema at server boot; the migration only ensures the schema exists.
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        await Assert.That(await SchemaExistsAsync(connStr, "hangfire")).IsTrue();
    }

    [Test]
    public async Task ConsolidatedSchema_HasNetCumulativeShape_AfterFullChain()
    {
        // Guards the migration consolidation: the two-file chain must reproduce the exact net
        // shape of the former multi-migration timeline. Tables dropped along that timeline must be
        // absent, tables and columns added across it must be present, and the nullability that
        // downstream code depends on must hold.
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        // Tables removed over the original timeline must not exist.
        await Assert.That(await TableExistsAsync(connStr, "MachineCertificates")).IsFalse();
        await Assert.That(await TableExistsAsync(connStr, "WebhookEndpoints")).IsFalse();

        // Tables introduced over the original timeline must exist.
        await Assert.That(await TableExistsAsync(connStr, "MachineAuthorizedKeys")).IsTrue();
        await Assert.That(await TableExistsAsync(connStr, "IntegrationEndpoints")).IsTrue();
        await Assert.That(await TableExistsAsync(connStr, "AlertRuleMachines")).IsTrue();
        await Assert.That(await TableExistsAsync(connStr, "TierFeatureLimits")).IsTrue();
        await Assert.That(await TableExistsAsync(connStr, "TenantSubscriptionOverrides")).IsTrue();

        // Columns added over the timeline must be present; dropped columns must be gone.
        await Assert.That(await ColumnExistsAsync(connStr, "RegistrationTokens", "ExpiresAt")).IsTrue();
        await Assert.That(await ColumnExistsAsync(connStr, "TenantSubscriptions", "CancelAtPeriodEnd")).IsTrue();
        await Assert.That(await ColumnExistsAsync(connStr, "TenantSubscriptions", "MachineLimit")).IsFalse();
        await Assert.That(await ColumnExistsAsync(connStr, "TenantSubscriptions", "PendingAction")).IsFalse();

        // IntegrationDeliveryAttempts.SucceededAt is nullable in the net schema.
        await Assert.That(await ColumnIsNullableAsync(connStr, "IntegrationDeliveryAttempts", "SucceededAt")).IsTrue();
    }

    [Test]
    public async Task TierFeatureLimits_SeededWithExactPerTierValues_AfterFullChain()
    {
        // The TierFeatureLimits seed was folded from its own migration into InitialMigration's seed
        // section. Assert exact per-tier values (not just a row count) so a seed regression is caught.
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        await Assert.That(await CountRowsAsync(connStr, "TierFeatureLimits")).IsEqualTo(3L);

        // Tier 1 (Free), Tier 2 (Pro), Tier 3 (Team): MachineLimit, RetentionDays, AlertRuleLimit, WebhookLimit, MemberLimit.
        await Assert.That(await ReadTierLimitsAsync(connStr, 1)).IsEqualTo("3,1,0,0,1");
        await Assert.That(await ReadTierLimitsAsync(connStr, 2)).IsEqualTo("1000,60,10,5,5");
        await Assert.That(await ReadTierLimitsAsync(connStr, 3)).IsEqualTo($"10000,365,25,15,{int.MaxValue}");
    }

    [Test]
    public async Task PartitionedTables_ArePartitioned_AfterFullChain()
    {
        // Four tables are partitioned on Postgres and plain on SQLite, so the SQLite runs cannot
        // see this at all. A table that silently stays unpartitioned passes every existence and
        // column check while breaking the partition-maintenance services at runtime.
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        // relkind 'p' is a partitioned table; 'r' is an ordinary one.
        await Assert.That(await GetRelkindAsync(connStr, "MachineTelemetry")).IsEqualTo('p');
        await Assert.That(await GetRelkindAsync(connStr, "AuditLog")).IsEqualTo('p');
        await Assert.That(await GetRelkindAsync(connStr, "AlertEvents")).IsEqualTo('p');
        await Assert.That(await GetRelkindAsync(connStr, "RemoteCommands")).IsEqualTo('p');

        // MachineTelemetry is two-level: LIST("RetentionClass") then RANGE("ReceivedAt") per class,
        // so each retention-class partition must itself be partitioned.
        await Assert.That(await GetRelkindAsync(connStr, "MachineTelemetry_Short")).IsEqualTo('p');
        await Assert.That(await GetRelkindAsync(connStr, "MachineTelemetry_Medium")).IsEqualTo('p');
        await Assert.That(await GetRelkindAsync(connStr, "MachineTelemetry_Long")).IsEqualTo('p');

        // Each range parent carries a default partition so a row outside the current window is
        // never rejected outright.
        await Assert.That(await GetPartitionNamesAsync(connStr, "AuditLog"))
            .Contains("auditlog_default");
        await Assert.That(await GetPartitionNamesAsync(connStr, "AlertEvents"))
            .Contains("alertevents_default");
        await Assert.That(await GetPartitionNamesAsync(connStr, "RemoteCommands"))
            .Contains("remotecommands_default");
    }

    [Test]
    public async Task PostgresOnlyIndexes_HaveTheirIntendedDefinitions_AfterFullChain()
    {
        // Existence is not enough: four of these indexes exist under the same name on both
        // dialects with materially different definitions, so an existence check passes on a wrong one.
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        // Case-insensitive uniqueness is LOWER("Name") on Postgres and COLLATE NOCASE on SQLite.
        string? tenantsName = await GetIndexDefAsync(connStr, "IX_Tenants_Name");
        await Assert.That(tenantsName).IsNotNull();
        await Assert.That(tenantsName!).Contains("UNIQUE");
        await Assert.That(tenantsName).Contains("lower");

        // The Postgres definition carries the partition key columns; the SQLite one is single-column.
        string? sourceEventId = await GetIndexDefAsync(connStr, "IX_MachineTelemetry_SourceEventId");
        await Assert.That(sourceEventId).IsNotNull();
        await Assert.That(sourceEventId!).Contains("UNIQUE");
        await Assert.That(sourceEventId).Contains("RetentionClass");
        await Assert.That(sourceEventId).Contains("ReceivedAt");
        await Assert.That(sourceEventId).Contains("WHERE");

        // Partial indexes: the predicate is the whole point, and a dropped WHERE clause changes
        // uniqueness semantics rather than just performance.
        string? activeRoles = await GetIndexDefAsync(connStr, "IX_UserTenantRoles_Active");
        await Assert.That(activeRoles).IsNotNull();
        await Assert.That(activeRoles!).Contains("UNIQUE");
        await Assert.That(activeRoles).Contains("WHERE");

        string? activeMachines = await GetIndexDefAsync(connStr, "IX_Machines_TenantId_Active");
        await Assert.That(activeMachines).IsNotNull();
        await Assert.That(activeMachines!).Contains("WHERE");

        foreach (string indexName in new[]
        {
            "IX_TenantDeletions_ActiveTenant",
            "IX_RemoteCommands_CommandId",
            "IX_IntegrationEndpoints_TenantId",
            "IX_IntegrationEndpoints_TenantId_Provider",
            "IX_MachineStateSummary_Hostname_Trgm",
            "IX_MachineStateSummary_Name_Trgm",
            "IX_MachineStateSummary_HardwareModel_Trgm",
        })
        {
            await Assert.That(await GetIndexDefAsync(connStr, indexName)).IsNotNull();
        }
    }

    [Test]
    public async Task PgTrgmExtension_Installed_AfterFullChain()
    {
        // The three trigram indexes depend on it and it has no SQLite counterpart, so nothing
        // else in the suite would notice if the CREATE EXTENSION stopped running.
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        await Assert.That(await ExtensionExistsAsync(connStr, "pg_trgm")).IsTrue();
    }

    [Test]
    public async Task CheckConstraints_AndDeferrableForeignKeys_Exist_AfterFullChain()
    {
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        string? durationCheck = await GetConstraintDefAsync(connStr, "CK_AlertRules_DurationMinutes");
        await Assert.That(durationCheck).IsNotNull();
        await Assert.That(durationCheck!).Contains("CHECK");
        await Assert.That(durationCheck).Contains("DurationMinutes");

        // The UserAccounts self-references are raw SQL on Postgres purely so they can be
        // DEFERRABLE INITIALLY DEFERRED: the System row references itself, and without deferral a
        // --data-only restore requires disabling triggers. SQLite cannot express it, so losing the
        // deferral would be silent everywhere else.
        await Assert.That(await ForeignKeyIsDeferredAsync(connStr, "FK_Users_CreatedBy")).IsTrue();
        await Assert.That(await ForeignKeyIsDeferredAsync(connStr, "FK_Users_DeletedBy")).IsTrue();
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

    private static async Task<bool> ColumnIsNullableAsync(string connStr, string tableName, string columnName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @t AND column_name = @c";
        cmd.Parameters.AddWithValue("@t", tableName);
        cmd.Parameters.AddWithValue("@c", columnName);
        object? result = await cmd.ExecuteScalarAsync();

        return string.Equals(result?.ToString(), "YES", StringComparison.Ordinal);
    }

    private static async Task<long> CountRowsAsync(string connStr, string tableName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT COUNT(*) FROM ""{tableName}""";
        object? result = await cmd.ExecuteScalarAsync();

        return result is long l ? l : Convert.ToInt64(result);
    }

    private static async Task<string> ReadTierLimitsAsync(string connStr, int tier)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""MachineLimit"", ""RetentionDays"", ""AlertRuleLimit"", ""WebhookLimit"", ""MemberLimit""
            FROM ""TierFeatureLimits"" WHERE ""Tier"" = @tier";
        cmd.Parameters.AddWithValue("@tier", tier);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync() == false)
        {
            return string.Empty;
        }

        return $"{Convert.ToInt64(reader.GetValue(0))},{Convert.ToInt64(reader.GetValue(1))},{Convert.ToInt64(reader.GetValue(2))},{Convert.ToInt64(reader.GetValue(3))},{Convert.ToInt64(reader.GetValue(4))}";
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

    private static async Task<char?> GetRelkindAsync(string connStr, string relName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.relkind
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema() AND c.relname = @r";
        cmd.Parameters.AddWithValue("@r", relName);
        object? result = await cmd.ExecuteScalarAsync();

        return result is char c ? c : null;
    }

    private static async Task<List<string>> GetPartitionNamesAsync(string connStr, string parent)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT child.relname
            FROM pg_inherits i
            JOIN pg_class parent ON parent.oid = i.inhparent
            JOIN pg_class child ON child.oid = i.inhrelid
            JOIN pg_namespace n ON n.oid = parent.relnamespace
            WHERE n.nspname = current_schema() AND parent.relname = @p
            ORDER BY child.relname";
        cmd.Parameters.AddWithValue("@p", parent);
        List<string> names = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<string?> GetIndexDefAsync(string connStr, string indexName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT indexdef FROM pg_indexes
            WHERE schemaname = current_schema() AND indexname = @i";
        cmd.Parameters.AddWithValue("@i", indexName);
        object? result = await cmd.ExecuteScalarAsync();

        return result is null or DBNull ? null : result.ToString();
    }

    private static async Task<bool> ExtensionExistsAsync(string connStr, string extension)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = @e)";
        cmd.Parameters.AddWithValue("@e", extension);
        object? result = await cmd.ExecuteScalarAsync();

        return result is bool b && b;
    }

    private static async Task<string?> GetConstraintDefAsync(string connStr, string constraintName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT pg_get_constraintdef(c.oid)
            FROM pg_constraint c
            JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname = current_schema() AND c.conname = @c";
        cmd.Parameters.AddWithValue("@c", constraintName);
        object? result = await cmd.ExecuteScalarAsync();

        return result is null or DBNull ? null : result.ToString();
    }

    private static async Task<bool> ForeignKeyIsDeferredAsync(string connStr, string constraintName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.condeferrable AND c.condeferred
            FROM pg_constraint c
            JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname = current_schema() AND c.conname = @c AND c.contype = 'f'";
        cmd.Parameters.AddWithValue("@c", constraintName);
        object? result = await cmd.ExecuteScalarAsync();

        return result is bool b && b;
    }
}
