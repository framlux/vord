// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Background service that reports machine usage counts to the billing API every hour.
/// This serves as the primary metered billing reporting mechanism alongside event-driven
/// reports on machine registration and removal. Uses a distributed lock to ensure only
/// one instance runs in a Kubernetes cluster.
/// </summary>
public sealed class UsageHeartbeatService : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(90);
    private const string LockKey = "lock:usage-heartbeat";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBillingApiClient _billingApiClient;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<UsageHeartbeatService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="UsageHeartbeatService"/> class.
    /// </summary>
    public UsageHeartbeatService(
        IServiceScopeFactory scopeFactory,
        IBillingApiClient billingApiClient,
        IDistributedLock distributedLock,
        ILogger<UsageHeartbeatService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(billingApiClient);
        ArgumentNullException.ThrowIfNull(distributedLock);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _billingApiClient = billingApiClient;
        _distributedLock = distributedLock;
        _logger = logger;
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
                    _logger.LogDebug("Usage heartbeat: another instance holds the lock, skipping this cycle");
                }
                else
                {
                    await ReportUsageForAllPaidTenantsAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during usage heartbeat cycle");
            }

            await Task.Delay(HeartbeatInterval, stoppingToken);
        }
    }

    private async Task ReportUsageForAllPaidTenantsAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ISubscriptionRepository subscriptionRepository = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
        ITenantRepository tenantRepository = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        ISubscriptionService subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

        List<TenantSubscription> paidSubscriptions = await subscriptionRepository.GetPaidSubscriptionsAsync(ct);

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
                Tenant? tenant = await tenantRepository.GetTenantByIdAsync(subscription.TenantId, ct);
                if (tenant is null)
                {
                    _logger.LogWarning(
                        "Usage heartbeat: Tenant {TenantId} not found, skipping",
                        subscription.TenantId);

                    continue;
                }

                int machineCount = await subscriptionService.GetMachineCountForTenantAsync(subscription.TenantId, ct);
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
