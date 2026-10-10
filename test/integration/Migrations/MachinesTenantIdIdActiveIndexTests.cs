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
/// Verifies the index that serves the per-tenant quota cutoff lookup: a partial index over a
/// tenant's registered machines ordered by id, so the lookup can offset into one tenant's rows
/// without walking the primary key across every other tenant's machines.
/// </summary>
public sealed class MachinesTenantIdIdActiveIndexTests
{
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
    public async Task Index_CoversTenantAndIdForRegisteredMachinesOnly()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            @"SELECT indexdef FROM pg_indexes WHERE tablename = 'Machines' AND indexname = 'IX_Machines_TenantId_Id_Active'",
            connection);

        string? definition = await command.ExecuteScalarAsync() as string;

        await Assert.That(definition).IsNotNull();
        await Assert.That(definition!).Contains("(\"TenantId\", \"Id\")");
        await Assert.That(definition!).Contains("WHERE (\"IsDeleted\" = false)");
    }
}
