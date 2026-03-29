// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// FastEndpoints global pre-processor that enforces read-only access for tenants with canceled subscriptions.
/// GET requests are always allowed. POST/PUT/DELETE requests to non-billing endpoints return 403.
/// </summary>
public sealed class SubscriptionStatusPreProcessor : IGlobalPreProcessor
{
    /// <inheritdoc/>
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        HttpContext httpContext = context.HttpContext;
        string method = httpContext.Request.Method;

        // Allow GET requests (read-only access is always permitted)
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Skip billing endpoints so users can reactivate or manage their subscription
        string path = httpContext.Request.Path.Value ?? string.Empty;
        if (path.Contains("/billing/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Skip admin endpoints so system administrators can still operate
        if (path.Contains("/admin/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Skip authentication endpoints
        if (path.Contains("/auth/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Skip onboarding endpoints
        if (path.Contains("/onboarding/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Only enforce for authenticated users with a tenant context
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(httpContext.User, httpContext);
        if (tenantId is null)
        {
            return;
        }

        ISubscriptionService subscriptionService = httpContext.RequestServices
            .GetRequiredService<ISubscriptionService>();

        TenantSubscription? subscription = await subscriptionService
            .GetSubscriptionForTenantAsync(tenantId.Value, ct);

        if (subscription is null)
        {
            return;
        }

        if (subscription.Status == SubscriptionStatus.Canceled)
        {
            httpContext.Response.StatusCode = 403;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = "Subscription is canceled. Your account is in read-only mode. Please reactivate from the billing page."
            }, ct);

            context.HttpContext.MarkResponseStart();
        }
    }
}
