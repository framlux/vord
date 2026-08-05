// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;
using FluentMigrator.Infrastructure;
using FluentMigrator.Runner;
using Framlux.FleetManagement.Database.Migrations;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Framlux.FleetManagement.Test.Migrations;

/// <summary>
/// Guards the invariant that no migration is silently a no-op on the production dialect.
/// </summary>
/// <remarks>
/// A migration whose statements are all guarded by a dialect string the processor does not
/// recognise runs nothing and still records itself as applied — it throws nothing, so no
/// schema test and no smoke test can see it. That is how a partitioning migration in
/// vord-internal shipped and did nothing for four months.
///
/// The trap is that <c>IfDatabase("Postgres")</c> looks right: it is the name of the base
/// <c>PostgresProcessor</c>. But <c>AddPostgres()</c> resolves to <c>Postgres15_0Processor</c>,
/// whose type is <c>PostgreSQL15_0</c> with aliases <c>PostgreSQL15_0</c> and <c>PostgreSQL</c>.
/// Only the latter two match.
///
/// This builds the same processor production builds, so it tracks whatever
/// <c>AddPostgres()</c> resolves to rather than hard-coding the alias list under test.
/// </remarks>
public class MigrationDialectGuardTests
{
    [Test]
    public async Task EveryMigration_ProducesAtLeastOneExpression_OnPostgres()
    {
        using ServiceProvider services = BuildPostgresServices();
        using IServiceScope scope = services.CreateScope();
        IMigrationProcessor processor = scope.ServiceProvider.GetRequiredService<IMigrationProcessor>();

        List<string> silentMigrations = [];

        foreach (Type migrationType in GetMigrationTypes())
        {
            IMigration migration = (IMigration)Activator.CreateInstance(migrationType)!;
            MigrationContext context = new(processor, scope.ServiceProvider, connection: null);

            migration.GetUpExpressions(context);

            if (context.Expressions.Count == 0)
            {
                silentMigrations.Add(migrationType.Name);
            }
        }

        await Assert.That(silentMigrations).IsEmpty();
    }

    [Test]
    public async Task MigrationAssembly_ContainsMigrations()
    {
        // Without this, the guard above passes vacuously if migration discovery ever breaks.
        await Assert.That(GetMigrationTypes()).IsNotEmpty();
    }

    private static ServiceProvider BuildPostgresServices()
    {
        // Mirrors src/migrationRunner/Program.cs. Resolving the processor opens no connection,
        // and IfDatabase is evaluated from the processor's declared dialect alone, so the
        // connection string is never used.
        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString("Host=127.0.0.1;Database=unused")
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations());

        return services.BuildServiceProvider();
    }

    private static List<Type> GetMigrationTypes()
    {
        return [.. typeof(InitialMigration).Assembly.GetTypes()
            .Where(t => typeof(IMigration).IsAssignableFrom(t)
                && t.IsAbstract == false
                && t.GetCustomAttribute<MigrationAttribute>() is not null)
            .OrderBy(t => t.GetCustomAttribute<MigrationAttribute>()!.Version)];
    }
}
