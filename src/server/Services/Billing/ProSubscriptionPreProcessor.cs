// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// FastEndpoints global pre-processor that gates endpoints tagged
/// <see cref="EndpointTags.RequiresProSubscription"/> behind an active Pro or Team subscription.
/// Consolidates the previously-duplicated "load subscription; if null/Free/non-Active → 403" block
/// that appeared in every alert endpoint. Tenant resolution failures are left to the endpoint's own
/// handling so the existing 401 behavior is preserved. The subscription lookup goes through the
/// cached subscription repository, so this adds no extra database round-trip on the hot path.
/// </summary>
public sealed class ProSubscriptionPreProcessor : IGlobalPreProcessor
{
    /// <summary>The message returned when a tenant lacks an active Pro or Team subscription.</summary>
    public const string RequiresProMessage = "Alerting requires a Pro or Team subscription";

    /// <inheritdoc/>
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        HttpContext httpContext = context.HttpContext;

        // An earlier pre-processor (e.g. the canceled-subscription read-only gate) may have already
        // written a 403 response. Do not write again — a second write corrupts the response stream.
        if (httpContext.ResponseStarted())
        {
            return;
        }

        Endpoint? endpoint = httpContext.GetEndpoint();
        EndpointDefinition? epDef = endpoint?.Metadata?.GetMetadata<EndpointDefinition>();
        if ((epDef is null) || epDef.EndpointTags?.Contains(EndpointTags.RequiresProSubscription) != true)
        {
            return;
        }

        // No tenant context — defer to the endpoint, which emits its own 401.
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(httpContext.User, httpContext);
        if (tenantId is null)
        {
            return;
        }

        ISubscriptionService subscriptionService = httpContext.RequestServices
            .GetRequiredService<ISubscriptionService>();

        TenantSubscription? subscription = await subscriptionService
            .GetSubscriptionForTenantAsync(tenantId.Value, ct);

        if (RequiresProGate(subscription))
        {
            httpContext.Response.StatusCode = 403;
            await httpContext.Response.WriteAsJsonAsync(ApiResponse<object>.Error(RequiresProMessage), ct);

            context.HttpContext.MarkResponseStart();
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied subscription does not grant alerting access — i.e. it
    /// is missing, on the Free tier, or not in the Active status. Extracted as an
    /// <c>internal static</c> method so the gating decision can be unit-tested directly.
    /// </summary>
    /// <param name="subscription">The tenant's subscription, or <c>null</c> if none exists.</param>
    /// <returns><c>true</c> if access must be denied; otherwise <c>false</c>.</returns>
    internal static bool RequiresProGate(TenantSubscription? subscription)
    {
        return (subscription is null) ||
               (subscription.Tier == SubscriptionTier.Free) ||
               (subscription.Status != SubscriptionStatus.Active);
    }
}
