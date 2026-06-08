// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Hangfire job that recomputes machine health status for every machine in a single tenant and
/// applies the staleness sweep. Replaces the per-tenant iteration loop inside the former
/// <c>HealthSweepService</c>. A coordinator job enqueues one instance of this job per active
/// tenant on every health-sweep tick.
/// </summary>
public sealed class HealthSweepTenantJob
{
    private const string LockKeyPrefix = LockNames.HealthSweepTenantPrefix;

    private readonly IMachineStateRepository _machineStateRepository;
    private readonly ISqlDialect _dialect;
    private readonly ServerConfigurationService _configService;
    private readonly IAdvisoryLockProvider _advisoryLockProvider;
    private readonly ILogger<HealthSweepTenantJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="HealthSweepTenantJob"/> class.
    /// </summary>
    /// <param name="machineStateRepository">Repository used to run the per-tenant sweep SQL.</param>
    /// <param name="dialect">SQL dialect providing the per-tenant sweep query.</param>
    /// <param name="configService">Source of the current online-threshold setting.</param>
    /// <param name="advisoryLockProvider">Provides per-tenant exclusive coordination across replicas.</param>
    /// <param name="logger">The logger.</param>
    public HealthSweepTenantJob(
        IMachineStateRepository machineStateRepository,
        ISqlDialect dialect,
        ServerConfigurationService configService,
        IAdvisoryLockProvider advisoryLockProvider,
        ILogger<HealthSweepTenantJob> logger)
    {
        ArgumentNullException.ThrowIfNull(machineStateRepository);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(advisoryLockProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _machineStateRepository = machineStateRepository;
        _dialect = dialect;
        _configService = configService;
        _advisoryLockProvider = advisoryLockProvider;
        _logger = logger;
    }

    /// <summary>
    /// Sweeps health status for every machine belonging to <paramref name="tenantId"/>.
    /// </summary>
    /// <param name="tenantId">The tenant whose machines to sweep. Must be positive.</param>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("critical")]
    public async Task RunAsync(int tenantId, CancellationToken ct)
    {
        if (tenantId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tenantId), "Tenant id must be positive.");
        }

        string lockKey = LockKeyPrefix + tenantId;
        await using IAsyncDisposable? lockHandle = await _advisoryLockProvider.TryAcquireAsync(lockKey, ct);

        if (lockHandle is null)
        {
            // Another replica is already sweeping this tenant. Skipping is safe — the recurring
            // coordinator will enqueue a fresh attempt on the next tick.
            return;
        }

        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);

        int rowsAffected = await _machineStateRepository.SweepHealthStatusAsync(
            _dialect.HealthSweepForTenant,
            tenantId,
            (int)onlineThreshold.TotalSeconds,
            ct);

        if (rowsAffected > 0)
        {
            _logger.LogDebug("Health sweep updated {Count} machine state rows for tenant {TenantId}", rowsAffected, tenantId);
        }
    }
}
