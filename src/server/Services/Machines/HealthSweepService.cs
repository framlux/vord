// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Periodically recomputes HealthStatus for all machines, partitioned by tenant.
/// Each tenant gets its own distributed lock so multiple replicas can process
/// different tenants concurrently. Also handles offline detection (staleness sweep):
/// machines that have not sent telemetry within the configured threshold are
/// transitioned to Offline status.
/// </summary>
public sealed class HealthSweepService : BackgroundService
{
    /// <summary>
    /// How often to run the health sweep across all tenants.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(30);
    private const string LockKeyPrefix = "lock:health-sweep:";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISqlDialect _dialect;
    private readonly ServerConfigurationService _configService;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<HealthSweepService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="HealthSweepService"/> class.
    /// </summary>
    public HealthSweepService(
        IServiceScopeFactory scopeFactory,
        ISqlDialect dialect,
        ServerConfigurationService configService,
        IDistributedLock distributedLock,
        ILogger<HealthSweepService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _distributedLock = distributedLock ?? throw new ArgumentNullException(nameof(distributedLock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        _logger.LogInformation("Health sweep service started");

        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await SweepAllTenantsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health sweep cycle");
            }

            await Task.Delay(Interval, stoppingToken);
        }

        _logger.LogInformation("Health sweep service stopped");
    }

    /// <summary>
    /// Iterates over all distinct tenants and runs a per-tenant health sweep
    /// with distributed locking so multiple replicas can process different tenants concurrently.
    /// </summary>
    internal async Task SweepAllTenantsAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IMachineStateRepository machineStateRepo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);

        List<int> tenantIds = await machineStateRepo.GetDistinctTenantIdsAsync(ct);

        int totalUpdated = 0;

        foreach (int tenantId in tenantIds)
        {
            string lockKey = LockKeyPrefix + tenantId;
            await using LockHandle? lockHandle = await _distributedLock.TryAcquireAsync(lockKey, LockTtl);

            if (lockHandle is null)
            {
                continue;
            }

            int rowsAffected = await machineStateRepo.SweepHealthStatusAsync(
                _dialect.HealthSweepForTenant,
                tenantId,
                (int)onlineThreshold.TotalSeconds,
                ct);
            totalUpdated += rowsAffected;
        }

        if (totalUpdated > 0)
        {
            _logger.LogDebug("Health sweep updated {Count} machine state rows across {TenantCount} tenants", totalUpdated, tenantIds.Count);
        }
    }
}
