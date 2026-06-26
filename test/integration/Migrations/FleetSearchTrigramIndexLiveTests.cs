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
/// Verifies the pg_trgm GIN indexes exist after the full migration chain runs against a real
/// Postgres. pg_trgm is unavailable on the SQLite unit-test database, so this lives in integration.
/// </summary>
public sealed class FleetSearchTrigramIndexLiveTests
{
    private static PostgresFixture _fixture = default!;

    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();
    }

    [After(Class)]
    public static async Task AfterClass() => await _fixture.DisposeAsync();

    [Test]
    public async Task TrigramIndexesExistAfterMigration()
    {
        using ServiceProvider sp = BuildRunner(_fixture.ConnectionString);
        sp.GetRequiredService<IMigrationRunner>().MigrateUp();

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM pg_indexes WHERE indexname IN " +
            "('IX_MachineStateSummary_Name_Trgm','IX_MachineStateSummary_Hostname_Trgm','IX_MachineStateSummary_HardwareModel_Trgm')";
        long count = (long)(await cmd.ExecuteScalarAsync())!;

        await Assert.That(count).IsEqualTo(3L);
    }

    private static ServiceProvider BuildRunner(string connectionString)
    {
        ServiceCollection services = new();
        services.AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Warning));

        return services.BuildServiceProvider();
    }
}
