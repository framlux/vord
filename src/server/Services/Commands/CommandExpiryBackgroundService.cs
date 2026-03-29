// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;

namespace Framlux.FleetManagement.Server.Services.Commands;

/// <summary>
/// Background service that periodically expires pending remote commands that have passed their ExpiresAt time.
/// </summary>
public sealed class CommandExpiryBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CommandExpiryBackgroundService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandExpiryBackgroundService"/> class.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory for creating scoped database contexts</param>
    /// <param name="logger">The logger</param>
    public CommandExpiryBackgroundService(IServiceScopeFactory scopeFactory, ILogger<CommandExpiryBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                IDatabaseCache cache = scope.ServiceProvider.GetRequiredService<IDatabaseCache>();
                await cache.ExpirePendingCommandsAsync(stoppingToken);

                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error expiring pending commands");
            }
        }
    }
}
