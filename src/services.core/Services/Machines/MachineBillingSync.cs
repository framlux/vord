// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Synchronizes active machine counts to the billing provider after machine lifecycle changes.
/// </summary>
public sealed class MachineBillingSync : IMachineBillingSync
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly ITenantRepository _tenantRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly IBillingApiClient _billingApiClient;
    private readonly ILogger<MachineBillingSync> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MachineBillingSync"/> class.
    /// </summary>
    /// <param name="subscriptionService">The subscription service.</param>
    /// <param name="tenantRepo">The tenant repository.</param>
    /// <param name="machineRepo">The machine repository.</param>
    /// <param name="billingApiClient">The billing API client.</param>
    /// <param name="logger">The logger.</param>
    public MachineBillingSync(
        ISubscriptionService subscriptionService,
        ITenantRepository tenantRepo,
        IMachineRepository machineRepo,
        IBillingApiClient billingApiClient,
        ILogger<MachineBillingSync> logger)
    {
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(tenantRepo);
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(billingApiClient);
        ArgumentNullException.ThrowIfNull(logger);

        _subscriptionService = subscriptionService;
        _tenantRepo = tenantRepo;
        _machineRepo = machineRepo;
        _billingApiClient = billingApiClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task ReportActiveMachineUsageAsync(int tenantId, CancellationToken ct)
    {
        // Report billable quantity to billing (best effort)
        try
        {
            TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);

            // Only report quantity for genuinely billable tiers. An allowlist (Pro/Team) rather
            // than excluding Free alone, because a subscription row can also carry Tier.None
            // (e.g. one that predates a tier being set); None has no floor policy and would
            // otherwise reach GetBillableMachineCountAsync, which refuses it.
            if ((subscription is not null) &&
                ((subscription.Tier == SubscriptionTier.Pro) || (subscription.Tier == SubscriptionTier.Team)))
            {
                Tenant? tenant = await _tenantRepo.GetTenantByIdAsync(tenantId, ct);

                if (tenant is not null)
                {
                    int quantity = await _subscriptionService.GetBillableMachineCountAsync(
                        tenantId, subscription.Tier, ct);
                    await _billingApiClient.UpdateQuantityAsync(tenant.ExternalId, quantity, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to report billable quantity to billing for tenant {TenantId}",
                tenantId);
        }
    }
}
