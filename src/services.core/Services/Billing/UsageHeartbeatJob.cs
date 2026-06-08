// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Hangfire recurring job that reports machine usage counts to the billing API.
/// Serves as the primary metered billing reporting mechanism alongside event-driven reports
/// on machine registration and removal. Replaces the former UsageHeartbeatService.
/// </summary>
public sealed class UsageHeartbeatJob
{
    /// <summary>Advisory lock name for cross-replica heartbeat serialization.</summary>
    internal const string LockName = Framlux.FleetManagement.Services.Core.Infrastructure.LockNames.UsageHeartbeat;

    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly ITenantRepository _tenantRepository;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IBillingApiClient _billingApiClient;
    private readonly IAdvisoryLockProvider _lockProvider;
    private readonly ILogger<UsageHeartbeatJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="UsageHeartbeatJob"/> class.
    /// </summary>
    /// <param name="subscriptionRepository">The subscription repository.</param>
    /// <param name="tenantRepository">The tenant repository.</param>
    /// <param name="subscriptionService">The subscription service.</param>
    /// <param name="billingApiClient">The billing API client.</param>
    /// <param name="lockProvider">The advisory lock provider used to serialize across replicas.</param>
    /// <param name="logger">The logger.</param>
    public UsageHeartbeatJob(
        ISubscriptionRepository subscriptionRepository,
        ITenantRepository tenantRepository,
        ISubscriptionService subscriptionService,
        IBillingApiClient billingApiClient,
        IAdvisoryLockProvider lockProvider,
        ILogger<UsageHeartbeatJob> logger)
    {
        ArgumentNullException.ThrowIfNull(subscriptionRepository);
        ArgumentNullException.ThrowIfNull(tenantRepository);
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(billingApiClient);
        ArgumentNullException.ThrowIfNull(lockProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _subscriptionRepository = subscriptionRepository;
        _tenantRepository = tenantRepository;
        _subscriptionService = subscriptionService;
        _billingApiClient = billingApiClient;
        _lockProvider = lockProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs the usage heartbeat. Reports the current machine count for every paid tenant.
    /// Per-tenant errors are swallowed and counted; top-level errors propagate so Hangfire
    /// records the cycle as failed.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    // Serialize across replicas via Postgres advisory lock instead of
    // [DisableConcurrentExecution]. The lock is try-once: if another replica holds it we skip
    // this tick rather than queue up — the next hourly tick will retry and metered-billing
    // data tolerates a one-hour gap.
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken ct)
    {
        await using IAsyncDisposable? lockHandle = await _lockProvider.TryAcquireAsync(LockName, ct);
        if (lockHandle is null)
        {
            _logger.LogInformation("Usage heartbeat skipped — another replica holds the lock");

            return;
        }

        List<TenantSubscription> paidSubscriptions = await _subscriptionRepository.GetPaidSubscriptionsAsync(ct);

        if (paidSubscriptions.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Usage heartbeat: reporting usage for {Count} paid tenants", paidSubscriptions.Count);

        int successCount = 0;
        int failCount = 0;

        foreach (TenantSubscription subscription in paidSubscriptions)
        {
            try
            {
                Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(subscription.TenantId, ct);
                if (tenant is null)
                {
                    _logger.LogWarning(
                        "Usage heartbeat: Tenant {TenantId} not found, skipping",
                        subscription.TenantId);

                    continue;
                }

                int machineCount = await _subscriptionService.GetMachineCountForTenantAsync(subscription.TenantId, ct);
                bool success = await _billingApiClient.ReportMachineUsageAsync(tenant.ExternalId, machineCount, ct);

                if (success)
                {
                    successCount++;
                    _logger.LogDebug(
                        "Usage heartbeat: Reported {MachineCount} machines for tenant {TenantId}",
                        machineCount, subscription.TenantId);
                }
                else
                {
                    failCount++;
                    _logger.LogWarning(
                        "Usage heartbeat: Failed to report usage for tenant {TenantId}",
                        subscription.TenantId);
                }
            }
            catch (Exception ex)
            {
                failCount++;
                _logger.LogWarning(ex,
                    "Usage heartbeat: Error reporting usage for tenant {TenantId}",
                    subscription.TenantId);
            }
        }

        _logger.LogInformation(
            "Usage heartbeat: completed. Success: {SuccessCount}, Failed: {FailCount}",
            successCount, failCount);
    }
}
