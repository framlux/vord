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
/// Verifies the Enterprise tier migration: the fallback limits row, the member-limit override
/// column and the applied-agreement-revision column are present after migration.
/// </summary>
public sealed class EnterpriseTierMigrationTests
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

    [Test]
    public async Task EnterpriseLimitsRow_IsSeededWithTeamValuesAndNoBillableFloor()
    {
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = new(
            @"SELECT ""MachineLimit"", ""RetentionDays"", ""AlertRuleLimit"", ""WebhookLimit"", ""MemberLimit"", ""MinimumBillableMachines""
              FROM ""TierFeatureLimits"" WHERE ""Tier"" = 4", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();

        await Assert.That(await reader.ReadAsync()).IsTrue();
        await Assert.That(reader.GetInt32(0)).IsEqualTo(10000);
        await Assert.That(reader.GetInt32(1)).IsEqualTo(365);
        await Assert.That(reader.GetInt32(2)).IsEqualTo(25);
        await Assert.That(reader.GetInt32(3)).IsEqualTo(15);
        await Assert.That(reader.GetInt32(4)).IsEqualTo(int.MaxValue);
        await Assert.That(reader.GetInt32(5)).IsEqualTo(0);
    }

    [Test]
    [Arguments("TenantSubscriptionOverrides", "MemberLimit")]
    [Arguments("TenantSubscriptions", "AppliedAgreementRevision")]
    public async Task NewColumns_ExistAndAreNullable(string table, string column)
    {
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = new(
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = @t AND column_name = @c", conn);
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("c", column);

        object? nullable = await cmd.ExecuteScalarAsync();

        await Assert.That(nullable as string).IsEqualTo("YES");
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
