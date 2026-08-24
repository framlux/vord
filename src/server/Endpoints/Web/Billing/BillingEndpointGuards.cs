// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Deployment;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Shared preamble logic for the billing action endpoints.
/// </summary>
internal static class BillingEndpointGuards
{
    /// <summary>
    /// Runs the shared billing-action preamble: 404 when billing is disabled, 404 when the
    /// tenant has no subscription. Returns the subscription when the request may proceed,
    /// or null after having written the error response.
    /// </summary>
    internal static async Task<TenantSubscription?> LoadGatedSubscriptionAsync(
        HttpContext httpContext,
        DeploymentMode deploymentMode,
        ISubscriptionService subscriptionService,
        int tenantId,
        CancellationToken ct)
    {
        if (deploymentMode.IsSaas == false)
        {
            await httpContext.SendApiErrorAsync(404, "Billing is not enabled", ct);

            return null;
        }

        TenantSubscription? subscription = await subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
        if (subscription is null)
        {
            await httpContext.SendApiErrorAsync(404, "Subscription not found", ct);

            return null;
        }

        return subscription;
    }
}
