// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints;
using Framlux.FleetManagement.Server.Services.Billing;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.Server.Services.Tenancy;

/// <summary>
/// Resolves the current request's tenant scope exactly once and stores it in the scoped
/// <see cref="ITenantContext"/>. Endpoints tagged <see cref="EndpointTags.RequiresTenant"/> get a
/// 401 written here when no tenant scope exists, so their handlers can call
/// <see cref="ITenantContext.RequireTenantId"/> without a null check. Untagged endpoints are left
/// with a null tenant and keep handling tenant-less requests themselves.
/// </summary>
public sealed class TenantContextPreProcessor : IGlobalPreProcessor
{
    /// <inheritdoc/>
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        HttpContext httpContext = context.HttpContext;

        TenantContext tenantContext = httpContext.RequestServices.GetRequiredService<TenantContext>();

        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(httpContext.User, httpContext);
        int? userId = TenantClaimHelper.GetUserIdFromClaims(httpContext.User);
        tenantContext.Set(tenantId, userId);

        Endpoint? endpoint = httpContext.GetEndpoint();
        EndpointDefinition? epDef = endpoint?.Metadata?.GetMetadata<EndpointDefinition>();
        if (ShouldReject(epDef?.EndpointTags, tenantId) == false)
        {
            return;
        }

        // Another pre-processor may have already written a response; a second write would
        // corrupt the response stream.
        if (httpContext.ResponseStarted())
        {
            return;
        }

        await httpContext.SendApiErrorAsync(401, "Unauthorized", ct);
        context.HttpContext.MarkResponseStart();
    }

    /// <summary>
    /// Returns <c>true</c> when the request must be rejected with a 401 — i.e. the endpoint
    /// declares <see cref="EndpointTags.RequiresTenant"/> and no tenant scope was resolved.
    /// Extracted as an <c>internal static</c> method so the gating decision can be unit-tested
    /// directly.
    /// </summary>
    /// <param name="endpointTags">The endpoint's declared tags, or <c>null</c> when unresolved.</param>
    /// <param name="tenantId">The resolved tenant id, or <c>null</c> when the request has no tenant scope.</param>
    /// <returns><c>true</c> if the request must be rejected; otherwise <c>false</c>.</returns>
    internal static bool ShouldReject(IEnumerable<string>? endpointTags, int? tenantId)
    {
        if (tenantId is not null)
        {
            return false;
        }

        return (endpointTags is not null) && endpointTags.Contains(EndpointTags.RequiresTenant);
    }
}
