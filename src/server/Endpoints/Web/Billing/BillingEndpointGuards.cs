// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
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
    /// <summary>The answer to any self-serve billing change on an Enterprise tenant.</summary>
    internal const string EnterpriseAgreementMessage =
        "This organization is on an Enterprise agreement; contact us to change it.";

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

    /// <summary>
    /// Refuses a self-serve billing change for an Enterprise tenant, whose plan is set by its
    /// agreement rather than by Stripe.
    /// </summary>
    /// <returns><c>true</c> when the request was refused and a response has been sent.</returns>
    internal static async Task<bool> RefuseEnterpriseAsync(
        HttpContext httpContext, TenantSubscription subscription, CancellationToken ct)
    {
        if (subscription.Tier != SubscriptionTier.Enterprise)
        {
            return false;
        }

        await httpContext.SendApiErrorAsync(StatusCodes.Status409Conflict, EnterpriseAgreementMessage, ct);

        return true;
    }
}
