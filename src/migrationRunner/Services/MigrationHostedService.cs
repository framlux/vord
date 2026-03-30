// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.MigrationRunner.Services;

/// <summary>
/// Background service that executes database migrations after the web server starts listening.
/// </summary>
public sealed class MigrationHostedService : BackgroundService
{
    private readonly IDbMigrator _migrator;
    private readonly MigrationState _state;
    private readonly ILogger<MigrationHostedService> _log;

    /// <summary>
    /// Creates a new <see cref="MigrationHostedService"/> instance.
    /// </summary>
    /// <param name="migrator">The migration runner</param>
    /// <param name="state">The migration state machine</param>
    /// <param name="log">The app-wide logger</param>
    public MigrationHostedService(IDbMigrator migrator, MigrationState state, ILogger<MigrationHostedService> log)
    {
        _migrator = migrator;
        _state = state;
        _log = log;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("MigrationHostedService starting.");
        try
        {
            // Run on the thread pool so the synchronous MigrateUp() call does not
            // block the startup pipeline (Kestrel must begin listening first).
            await Task.Run(() => _migrator.RunAsync(stoppingToken), stoppingToken);
            _state.MarkSuccess();
            _log.LogInformation("Database migrations succeeded.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Database migrations failed.");
            _state.MarkFailure(ex);
        }
    }
}
