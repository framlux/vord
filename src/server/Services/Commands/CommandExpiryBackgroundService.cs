// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Commands;

/// <summary>
/// Background service that periodically expires pending remote commands that have passed their ExpiresAt time.
/// </summary>
public sealed class CommandExpiryBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(90);
    private const string LockKey = "lock:command-expiry";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<CommandExpiryBackgroundService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandExpiryBackgroundService"/> class.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory for creating scoped database contexts</param>
    /// <param name="distributedLock">Distributed lock to ensure only one replica runs expiry at a time</param>
    /// <param name="logger">The logger</param>
    public CommandExpiryBackgroundService(
        IServiceScopeFactory scopeFactory,
        IDistributedLock distributedLock,
        ILogger<CommandExpiryBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _distributedLock = distributedLock ?? throw new ArgumentNullException(nameof(distributedLock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await using LockHandle? lockHandle = await _distributedLock.TryAcquireAsync(LockKey, LockTtl);
                if (lockHandle is null)
                {
                    _logger.LogDebug("Command expiry: another instance holds the lock, skipping this cycle");
                }
                else
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    IDatabaseCache cache = scope.ServiceProvider.GetRequiredService<IDatabaseCache>();
                    await cache.ExpirePendingCommandsAsync(stoppingToken);
                }

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
