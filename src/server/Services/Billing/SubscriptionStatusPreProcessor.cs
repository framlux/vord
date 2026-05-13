// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// FastEndpoints global pre-processor that enforces read-only access for tenants with canceled subscriptions.
/// GET requests are always allowed. POST/PUT/DELETE requests return 403 unless the endpoint is tagged
/// with <see cref="EndpointTags.SubscriptionExempt"/>.
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

        // Skip endpoints tagged as subscription-exempt (billing, auth, admin, onboarding).
        // Endpoints opt in by calling Tags(EndpointTags.SubscriptionExempt) in Configure().
        Endpoint? endpoint = httpContext.GetEndpoint();
        EndpointDefinition? epDef = endpoint?.Metadata?.GetMetadata<EndpointDefinition>();
        if ((epDef is not null) && epDef.EndpointTags?.Contains(EndpointTags.SubscriptionExempt) == true)
        {
            return;
        }

        // Fallback: skip paths that match known exempt prefixes.
        // This handles non-FastEndpoints routes (middleware, health, etc.)
        string path = httpContext.Request.Path.Value ?? string.Empty;
        if (path.Contains("/billing/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/admin/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/auth/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/onboarding/", StringComparison.OrdinalIgnoreCase))
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
