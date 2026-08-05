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

        // relkind = 'p' alone only proves some relation by that name is partitioned; it does not
        // prove the shape or that Short/Medium/Long map to the right retention class. Pin the
        // partition strategy and the class each parent claims.
        await Assert.That(await GetPartitionKeyDefAsync(connStr, "MachineTelemetry")).IsEqualTo("LIST (\"RetentionClass\")");
        await Assert.That(await GetPartitionKeyDefAsync(connStr, "MachineTelemetry_Short")).IsEqualTo("RANGE (\"ReceivedAt\")");
        await Assert.That(await GetPartitionKeyDefAsync(connStr, "MachineTelemetry_Medium")).IsEqualTo("RANGE (\"ReceivedAt\")");
        await Assert.That(await GetPartitionKeyDefAsync(connStr, "MachineTelemetry_Long")).IsEqualTo("RANGE (\"ReceivedAt\")");

        // Pin which RetentionClass value each named parent actually owns. A Short/Long swap here
        // would misroute every retention class while every other assertion in this test still passes.
        await Assert.That(await GetPartitionBoundAsync(connStr, "MachineTelemetry_Short")).IsEqualTo("FOR VALUES IN ('0')");
        await Assert.That(await GetPartitionBoundAsync(connStr, "MachineTelemetry_Medium")).IsEqualTo("FOR VALUES IN ('1')");
        await Assert.That(await GetPartitionBoundAsync(connStr, "MachineTelemetry_Long")).IsEqualTo("FOR VALUES IN ('2')");

        // The top-level LIST parent has exactly the three class parents as children, no more, no less.
        await Assert.That(await GetPartitionNamesAsync(connStr, "MachineTelemetry"))
            .IsEquivalentTo(new List<string> { "MachineTelemetry_Long", "MachineTelemetry_Medium", "MachineTelemetry_Short" });

        // Each range parent's bootstrap creates a daily leaf for today plus the next 7 days
        // (8 total) and a default partition (9 total). Count and default-presence alone do not
        // prove the window runs forward: a reversed loop (today-7..today) produces the same count
        // and the same default, so pin the exact leaf names too. Derive "today" from the database's
        // own clock — CreateInitialDailyPartitions/CreateInitialClassDailyPartitions stamp names
        // from DateTime.UtcNow at migration time, so deriving expected names from the database's
        // now() (rather than a hardcoded date or the test host's clock) tracks the same day the
        // migration actually used, without ever hardcoding a date.
        DateOnly today = await GetDatabaseUtcTodayAsync(connStr);
        List<string> expectedDailyOffsets = [];
        for (int offset = 0; offset <= 7; offset++)
        {
            expectedDailyOffsets.Add(today.AddDays(offset).ToString("yyyyMMdd"));
        }

        List<string> auditLogChildren = await GetPartitionNamesAsync(connStr, "AuditLog");
        List<string> expectedAuditLogChildren = expectedDailyOffsets
            .Select(day => $"auditlog_d{day}")
            .Append("auditlog_default")
            .ToList();
        await Assert.That(auditLogChildren).IsEquivalentTo(expectedAuditLogChildren);

        List<string> alertEventsChildren = await GetPartitionNamesAsync(connStr, "AlertEvents");
        List<string> expectedAlertEventsChildren = expectedDailyOffsets
            .Select(day => $"alertevents_d{day}")
            .Append("alertevents_default")
            .ToList();
        await Assert.That(alertEventsChildren).IsEquivalentTo(expectedAlertEventsChildren);

        List<string> remoteCommandsChildren = await GetPartitionNamesAsync(connStr, "RemoteCommands");
        List<string> expectedRemoteCommandsChildren = expectedDailyOffsets
            .Select(day => $"remotecommands_d{day}")
            .Append("remotecommands_default")
            .ToList();
        await Assert.That(remoteCommandsChildren).IsEquivalentTo(expectedRemoteCommandsChildren);

        List<string> shortChildren = await GetPartitionNamesAsync(connStr, "MachineTelemetry_Short");
        List<string> expectedShortChildren = expectedDailyOffsets
            .Select(day => $"MachineTelemetry_Short_{day}")
            .Append("MachineTelemetry_Short_default")
            .ToList();
        await Assert.That(shortChildren).IsEquivalentTo(expectedShortChildren);

        List<string> mediumChildren = await GetPartitionNamesAsync(connStr, "MachineTelemetry_Medium");
        List<string> expectedMediumChildren = expectedDailyOffsets
            .Select(day => $"MachineTelemetry_Medium_{day}")
            .Append("MachineTelemetry_Medium_default")
            .ToList();
        await Assert.That(mediumChildren).IsEquivalentTo(expectedMediumChildren);

        List<string> longChildren = await GetPartitionNamesAsync(connStr, "MachineTelemetry_Long");
        List<string> expectedLongChildren = expectedDailyOffsets
            .Select(day => $"MachineTelemetry_Long_{day}")
            .Append("MachineTelemetry_Long_default")
            .ToList();
        await Assert.That(longChildren).IsEquivalentTo(expectedLongChildren);

        // PostgreSQL requires every partition-key column to be part of any unique constraint, which
        // is why these primary keys are composite. Column ORDER is load-bearing for index usage, so
        // compare the joined string rather than a set: IsEquivalentTo is order-insensitive and would
        // pass a reordered PK.
        await Assert.That(string.Join(",", await GetPrimaryKeyColumnsAsync(connStr, "MachineTelemetry")))
            .IsEqualTo("Id,RetentionClass,ReceivedAt");
        await Assert.That(string.Join(",", await GetPrimaryKeyColumnsAsync(connStr, "AuditLog")))
            .IsEqualTo("Id,Timestamp");
        await Assert.That(string.Join(",", await GetPrimaryKeyColumnsAsync(connStr, "AlertEvents")))
            .IsEqualTo("Id,TriggeredAt");
        await Assert.That(string.Join(",", await GetPrimaryKeyColumnsAsync(connStr, "RemoteCommands")))
            .IsEqualTo("Id,CreatedAt");
    }

    [Test]
    public async Task PostgresOnlyIndexes_HaveTheirIntendedDefinitions_AfterFullChain()
    {
        // Existence is not enough: five of these indexes exist under the same name on both
        // dialects with materially different definitions, so an existence check passes on a wrong
        // one. pg_indexes.indexdef is deterministic normalised text for a pinned Postgres image, so
        // the discriminating cases assert the full string rather than a keyword both dialects share.
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        // Case-insensitive uniqueness is lower("Name") on Postgres and COLLATE NOCASE on SQLite.
        string? tenantsName = await GetIndexDefAsync(connStr, "IX_Tenants_Name");
        await Assert.That(tenantsName).IsEqualTo(
            "CREATE UNIQUE INDEX \"IX_Tenants_Name\" ON public.\"Tenants\" USING btree (lower(\"Name\"))");

        // The Postgres definition carries the partition key columns and a NOT NULL predicate; the
        // SQLite one is single-column. "ON ONLY" reflects that the parent-level index has no rows
        // of its own on a partitioned table — the per-partition indexes carry the data.
        string? sourceEventId = await GetIndexDefAsync(connStr, "IX_MachineTelemetry_SourceEventId");
        await Assert.That(sourceEventId).IsEqualTo(
            "CREATE UNIQUE INDEX \"IX_MachineTelemetry_SourceEventId\" ON ONLY public.\"MachineTelemetry\" USING btree (\"SourceEventId\", \"RetentionClass\", \"ReceivedAt\") WHERE (\"SourceEventId\" IS NOT NULL)");

        // Partial indexes: the predicate is the whole point, and a dropped or wrong-column predicate
        // changes uniqueness semantics rather than just performance. Postgres renders boolean
        // literals as true/false where SQLite's own definition uses 1/0, so a Contains("WHERE")
        // could not tell a correct predicate from an inverted or wrong-column one; assert the exact text.
        string? activeRoles = await GetIndexDefAsync(connStr, "IX_UserTenantRoles_Active");
        await Assert.That(activeRoles).IsEqualTo(
            "CREATE UNIQUE INDEX \"IX_UserTenantRoles_Active\" ON public.\"UserTenantRoles\" USING btree (\"UserId\", \"AssignedTenantId\") WHERE (\"IsActive\" = true)");

        string? activeMachines = await GetIndexDefAsync(connStr, "IX_Machines_TenantId_Active");
        await Assert.That(activeMachines).IsEqualTo(
            "CREATE INDEX \"IX_Machines_TenantId_Active\" ON public.\"Machines\" USING btree (\"TenantId\") WHERE (\"IsDeleted\" = false)");

        // A fifth same-name/different-definition pair: Postgres carries the partition key
        // (CreatedAt) alongside CommandId; SQLite is CommandId alone.
        string? remoteCommandId = await GetIndexDefAsync(connStr, "IX_RemoteCommands_CommandId");
        await Assert.That(remoteCommandId).IsEqualTo(
            "CREATE UNIQUE INDEX \"IX_RemoteCommands_CommandId\" ON ONLY public.\"RemoteCommands\" USING btree (\"CommandId\", \"CreatedAt\")");

        // The trigram indexes have no SQLite counterpart at all: their entire value is
        // "USING gin (lower(col) gin_trgm_ops)". An index of the same name degraded to a plain
        // btree, or with lower() dropped, would pass an existence check and the pg_trgm extension
        // check, so pin the full definition including the column each one targets.
        string? nameTrgm = await GetIndexDefAsync(connStr, "IX_MachineStateSummary_Name_Trgm");
        await Assert.That(nameTrgm).IsEqualTo(
            "CREATE INDEX \"IX_MachineStateSummary_Name_Trgm\" ON public.\"MachineStateSummary\" USING gin (lower((\"Name\")::text) gin_trgm_ops)");

        string? hostnameTrgm = await GetIndexDefAsync(connStr, "IX_MachineStateSummary_Hostname_Trgm");
        await Assert.That(hostnameTrgm).IsEqualTo(
            "CREATE INDEX \"IX_MachineStateSummary_Hostname_Trgm\" ON public.\"MachineStateSummary\" USING gin (lower((\"Hostname\")::text) gin_trgm_ops)");

        string? hardwareModelTrgm = await GetIndexDefAsync(connStr, "IX_MachineStateSummary_HardwareModel_Trgm");
        await Assert.That(hardwareModelTrgm).IsEqualTo(
            "CREATE INDEX \"IX_MachineStateSummary_HardwareModel_Trgm\" ON public.\"MachineStateSummary\" USING gin (lower((\"HardwareModel\")::text) gin_trgm_ops)");

        // IX_IntegrationEndpoints_TenantId and _TenantId_Provider are byte-identical on both
        // dialects, so an existence check is all that's meaningful here.
        await Assert.That(await GetIndexDefAsync(connStr, "IX_TenantDeletions_ActiveTenant")).IsNotNull();
        await Assert.That(await GetIndexDefAsync(connStr, "IX_IntegrationEndpoints_TenantId")).IsNotNull();
        await Assert.That(await GetIndexDefAsync(connStr, "IX_IntegrationEndpoints_TenantId_Provider")).IsNotNull();
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

        // The bound is the entire semantic here: Contains("CHECK") + Contains("DurationMinutes")
        // would pass on ">= -100", "<= 0", or "<> 42" just as readily as the intended ">= 0". Assert
        // the full constraint definition.
        string? durationCheck = await GetConstraintDefAsync(connStr, "CK_AlertRules_DurationMinutes");
        await Assert.That(durationCheck).IsEqualTo("CHECK ((\"DurationMinutes\" >= 0))");

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

    private static async Task<DateOnly> GetDatabaseUtcTodayAsync(string connStr)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        // Matches the DateTime.UtcNow the migration itself stamps partition names from
        // (CreateInitialDailyPartitions / CreateInitialClassDailyPartitions), regardless of the
        // server's configured time zone.
        cmd.CommandText = "SELECT (now() AT TIME ZONE 'UTC')::date";
        object? result = await cmd.ExecuteScalarAsync();

        return (DateOnly)result!;
    }

    private static async Task<List<string>> GetPrimaryKeyColumnsAsync(string connStr, string tableName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT pg_get_constraintdef(c.oid)
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = current_schema() AND t.relname = @t AND c.contype = 'p'";
        cmd.Parameters.AddWithValue("@t", tableName);
        object? result = await cmd.ExecuteScalarAsync();
        if (result is null or DBNull)
        {
            return [];
        }

        // pg_get_constraintdef renders e.g. PRIMARY KEY ("Id", "RetentionClass", "ReceivedAt").
        // Extract the parenthesised column list in the order Postgres reports it and strip quotes.
        string constraintDef = result.ToString()!;
        int openParen = constraintDef.IndexOf('(');
        int closeParen = constraintDef.LastIndexOf(')');
        string columnList = constraintDef.Substring(openParen + 1, closeParen - openParen - 1);

        return columnList.Split(", ").Select(column => column.Trim('"')).ToList();
    }

    private static async Task<string?> GetPartitionKeyDefAsync(string connStr, string relName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT pg_get_partkeydef(c.oid)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema() AND c.relname = @r";
        cmd.Parameters.AddWithValue("@r", relName);
        object? result = await cmd.ExecuteScalarAsync();

        return result is null or DBNull ? null : result.ToString();
    }

    private static async Task<string?> GetPartitionBoundAsync(string connStr, string relName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT pg_get_expr(c.relpartbound, c.oid)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema() AND c.relname = @r";
        cmd.Parameters.AddWithValue("@r", relName);
        object? result = await cmd.ExecuteScalarAsync();

        return result is null or DBNull ? null : result.ToString();
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
