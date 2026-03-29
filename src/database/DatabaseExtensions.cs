// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database.Migrations;
using LinqToDB;
using LinqToDB.Extensions.DependencyInjection;
using LinqToDB.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Framlux.FleetManagement.Database;

/// <summary>
/// Extension methods for configuring database services and migrations.
/// </summary>
public static class DatabaseExtensions
{
    /// <summary>
    /// Adds the Fleet Management database services including LinqToDB context and FluentMigrator.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="config">The application configuration containing connection strings.</param>
    /// <param name="provider">The database provider to use. Defaults to PostgreSQL.</param>
    /// <returns>The updated service collection for method chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when required connection strings are not configured.</exception>
    public static IServiceCollection AddHomeAutomationDatabase(
        this IServiceCollection services,
        IConfiguration config,
        DatabaseProvider provider = DatabaseProvider.PostgreSQL)
    {
        services.AddLinqToDBContext<DatabaseContext>((serviceProvider, options) =>
        {
            string connectionString = config.GetConnectionString("DbConnectionString")
                ?? throw new InvalidOperationException("Connection string 'DbConnectionString' is not configured.");

            if (provider == DatabaseProvider.SQLite)
            {
                return options.UseSQLite(connectionString)
                       .UseDefaultLogging(serviceProvider);
            }

            return options.UsePostgreSQL(connectionString: connectionString, LinqToDB.DataProvider.PostgreSQL.PostgreSQLVersion.v18)
                   .UseDefaultLogging(serviceProvider);
        });

        services.AddFluentMigratorCore()
                .ConfigureRunner(r =>
                {
                    string connectionString = config.GetConnectionString("HomeAutomationDb")
                        ?? throw new InvalidOperationException("Connection string 'HomeAutomationDb' is not configured.");

                    if (provider == DatabaseProvider.SQLite)
                    {
                        r.AddSQLite()
                         .WithGlobalConnectionString(connectionString)
                         .ScanIn(typeof(MigrationVersion).Assembly);
                    }
                    else
                    {
                        r.AddPostgres()
                         .WithGlobalConnectionString(connectionString)
                         .ScanIn(typeof(MigrationVersion).Assembly);
                    }
                })
                .AddLogging(lb => lb.AddSerilog());

        return services;
    }

    /// <summary>
    /// Migrate the database to the latest version.
    /// </summary>
    /// <param name="serviceProvider">The app's ServiceProvider instance</param>
    /// <returns>Returns the Service Provider for fluent chaining</returns>
    public static IServiceProvider MigrateDatabase(this IServiceProvider serviceProvider)
    {
        using (IServiceScope scope = serviceProvider.CreateScope())
        {
            IMigrationRunner runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
        }

        return serviceProvider;
    }
}
