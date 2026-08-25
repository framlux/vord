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
/// Verifies the backfill that gives every pre-existing tenant a subscription row.
/// </summary>
/// <remarks>
/// Tenants created through the global-admin endpoint never got one, which was invisible until the
/// mutation gate started failing closed on a missing row. Nothing in the running system creates a
/// subscription for a tenant that already exists, so without this migration those tenants would be
/// permanently read-only with only manual SQL to recover.
/// </remarks>
public sealed class BackfillMissingTenantSubscriptionsTests
{
    private const int FreeTier = 0;
    private const int ActiveStatus = 1;

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
    /// The tenant without a row gets Free/Active; the tenant that already has a paid row keeps it
    /// untouched. Backfilling over an existing subscription would silently downgrade a paying
    /// customer, which is the one thing this migration must never do.
    /// </summary>
    [Test]
    public async Task Backfill_AddsRowForTenantsWithoutOne_AndLeavesExistingRowsAlone()
    {
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();

        // Migrate to the state immediately before the backfill, seed the broken rows the way the
        // shipped build could produce them, then run the backfill over that.
        IMigrationRunner runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp(new MigrationVersion(2026, 08, 08, 1).Version);

        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();

        int userId = await SeedUserAsync(conn);
        int tenantWithout = await SeedTenantAsync(conn, "Admin Created Tenant", userId);
        int tenantWith = await SeedTenantAsync(conn, "Onboarded Tenant", userId);
        await SeedSubscriptionAsync(conn, tenantWith, tier: 2, status: ActiveStatus);

        runner.MigrateUp();

        (int Tier, int Status, int Count) backfilled = await ReadSubscriptionAsync(conn, tenantWithout);
        await Assert.That(backfilled.Count).IsEqualTo(1);
        await Assert.That(backfilled.Tier).IsEqualTo(FreeTier);
        await Assert.That(backfilled.Status).IsEqualTo(ActiveStatus);

        (int Tier, int Status, int Count) untouched = await ReadSubscriptionAsync(conn, tenantWith);
        await Assert.That(untouched.Count).IsEqualTo(1);
        await Assert.That(untouched.Tier).IsEqualTo(2);
    }

    /// <summary>
    /// The insert is conditional, so running the full migration set against an already-migrated
    /// database must not add a second row — the TenantId unique constraint would reject it and the
    /// migration would fail on a rerun.
    /// </summary>
    [Test]
    public async Task Backfill_IsSafeWhenEveryTenantAlreadyHasASubscription()
    {
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();

        int userId = await SeedUserAsync(conn);
        int tenantId = await SeedTenantAsync(conn, "Already Provisioned", userId);
        await SeedSubscriptionAsync(conn, tenantId, FreeTier, ActiveStatus);

        await using NpgsqlCommand rerun = new(
            """
            INSERT INTO "TenantSubscriptions" ("TenantId", "Tier", "Status", "CreatedAt", "UpdatedAt", "CancelAtPeriodEnd")
            SELECT t."Id", 0, 1, NOW(), NOW(), FALSE
            FROM "Tenants" t
            WHERE NOT EXISTS (
                SELECT 1 FROM "TenantSubscriptions" s WHERE s."TenantId" = t."Id"
            );
            """, conn);
        int inserted = await rerun.ExecuteNonQueryAsync();

        await Assert.That(inserted).IsEqualTo(0);

        (int Tier, int Status, int Count) row = await ReadSubscriptionAsync(conn, tenantId);
        await Assert.That(row.Count).IsEqualTo(1);
    }

    /// <summary>
    /// The initial migration seeds a system account, which every tenant row can be attributed to.
    /// </summary>
    private static async Task<int> SeedUserAsync(NpgsqlConnection conn)
    {
        await using NpgsqlCommand cmd = new(
            """SELECT "Id" FROM "UserAccounts" WHERE "IsSystem" = TRUE ORDER BY "Id" LIMIT 1;""", conn);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int> SeedTenantAsync(NpgsqlConnection conn, string name, int userId)
    {
        await using NpgsqlCommand cmd = new(
            """
            INSERT INTO "Tenants" ("ExternalId", "Name", "LogoUrl", "IsActive", "CreatedAt", "CreatedByUserId")
            VALUES (@ext, @name, '', TRUE, NOW(), @userId)
            RETURNING "Id";
            """, conn);
        cmd.Parameters.AddWithValue("ext", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("name", $"{name} {Guid.NewGuid():N}");
        cmd.Parameters.AddWithValue("userId", userId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task SeedSubscriptionAsync(NpgsqlConnection conn, int tenantId, int tier, int status)
    {
        await using NpgsqlCommand cmd = new(
            """
            INSERT INTO "TenantSubscriptions" ("TenantId", "Tier", "Status", "CreatedAt", "UpdatedAt", "CancelAtPeriodEnd")
            VALUES (@tenantId, @tier, @status, NOW(), NOW(), FALSE);
            """, conn);
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("tier", tier);
        cmd.Parameters.AddWithValue("status", status);

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<(int Tier, int Status, int Count)> ReadSubscriptionAsync(
        NpgsqlConnection conn,
        int tenantId)
    {
        await using NpgsqlCommand cmd = new(
            """
            SELECT "Tier", "Status", COUNT(*) OVER () AS "RowCount"
            FROM "TenantSubscriptions"
            WHERE "TenantId" = @tenantId;
            """, conn);
        cmd.Parameters.AddWithValue("tenantId", tenantId);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync() == false)
        {
            return (-1, -1, 0);
        }

        return (reader.GetInt32(0), reader.GetInt32(1), Convert.ToInt32(reader.GetInt64(2)));
    }

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
        string baseConn = _fixture.ConnectionString;
        string dbName = $"it_{Guid.NewGuid():N}".ToLowerInvariant();
        NpgsqlConnectionStringBuilder template = new(baseConn);

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
}
