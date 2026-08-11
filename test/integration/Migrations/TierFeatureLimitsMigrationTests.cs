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
/// Verifies the per-tier billable machine floor is present and seeded after migration.
/// The floor is what stops a paid subscription sitting at zero machines.
/// </summary>
public sealed class TierFeatureLimitsMigrationTests
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
    [Arguments(1, 0)]
    [Arguments(2, 1)]
    [Arguments(3, 3)]
    public async Task MinimumBillableMachines_IsSeededPerTier(int tier, int expected)
    {
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();

        await using NpgsqlCommand cmd = new(
            @"SELECT ""MinimumBillableMachines"" FROM ""TierFeatureLimits"" WHERE ""Tier"" = @tier", conn);
        cmd.Parameters.AddWithValue("tier", tier);

        object? actual = await cmd.ExecuteScalarAsync();

        await Assert.That(actual).IsNotNull();
        await Assert.That(Convert.ToInt32(actual)).IsEqualTo(expected);
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
