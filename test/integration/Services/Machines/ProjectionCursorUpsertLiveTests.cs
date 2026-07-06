// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Integration;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// Live test that the projection-cursor upsert uses Postgres <c>INSERT ... ON CONFLICT DO UPDATE</c>
/// so two replicas advancing a brand-new shard's cursor concurrently cannot hit a primary-key
/// violation. SQLite cannot reproduce the concurrent-insert race, so this runs against real Postgres.
/// </summary>
public sealed class ProjectionCursorUpsertLiveTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    /// <summary>Starts Postgres and runs migrations once for the class.</summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _migratedConnectionString = _fixture.ConnectionString;

        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(_migratedConnectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Warning));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    /// <summary>Stops the container after the class.</summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    private static DatabaseContext CreateContext()
    {
        DataOptions<DatabaseContext> options = new(new DataOptions().UsePostgreSQL(_migratedConnectionString));

        return new DatabaseContext(options);
    }

    private static DatabaseRepository CreateRepo(DatabaseContext db) => new(db, NullLogger<DatabaseRepository>.Instance);

    [Test]
    public async Task ConcurrentUpsertOnNewShard_DoesNotThrow()
    {
        await using DatabaseContext db1 = CreateContext();
        await using DatabaseContext db2 = CreateContext();
        DatabaseRepository repo1 = CreateRepo(db1);
        DatabaseRepository repo2 = CreateRepo(db2);

        const int shard = 77; // fresh shard index — no cursor row exists yet

        // Both replicas advance the brand-new shard's cursor concurrently on independent connections.
        Task t1 = repo1.SetProjectionCursorAsync(shard, 100, shardCount: 1, CancellationToken.None);
        Task t2 = repo2.SetProjectionCursorAsync(shard, 200, shardCount: 1, CancellationToken.None);

        // ON CONFLICT means neither raises a duplicate-key violation.
        await Task.WhenAll(t1, t2);

        long? position = await repo1.GetProjectionCursorAsync(shard, CancellationToken.None);
        await Assert.That((position == 100L) || (position == 200L)).IsTrue();
        await Assert.That(await repo1.GetPersistedShardCountAsync(CancellationToken.None)).IsEqualTo(1);
    }
}
