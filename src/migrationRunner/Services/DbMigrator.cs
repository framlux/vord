// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;

namespace Framlux.FleetManagement.MigrationRunner.Services;

/// <summary>
/// Executes FluentMigrator migrations.
/// </summary>
public sealed class DbMigrator : IDbMigrator
{
    private readonly IServiceScopeFactory _factory;
    private readonly ILogger<DbMigrator> _log;

    /// <summary>
    /// Creates a new <see cref="DbMigrator"/> instance.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory</param>
    /// <param name="log">The app-wide logger</param>
    public DbMigrator(IServiceScopeFactory scopeFactory, ILogger<DbMigrator> log)
    {
        _factory = scopeFactory;
        _log = log;
    }

    /// <inheritdoc/>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("Creating service scope for migration runner...");
        using IServiceScope scope = _factory.CreateScope();
        IMigrationRunner runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        _log.LogInformation("Starting database migrations...");

        // FluentMigrator runner is synchronous; cancellation token cannot be passed directly.
        runner.MigrateUp();

        _log.LogInformation("Database migrations complete.");

        return Task.CompletedTask;
    }
}
