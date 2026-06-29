// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.Server.Services.Tenancy;

/// <summary>
/// Resolves the current request's tenant scope exactly once and stores it in the scoped
/// <see cref="ITenantContext"/>. Tenant-less requests are left with a null tenant so each endpoint
/// can emit its own 401 — this pre-processor never writes a response.
/// </summary>
public sealed class TenantContextPreProcessor : IGlobalPreProcessor
{
    /// <inheritdoc/>
    public Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        HttpContext httpContext = context.HttpContext;

        TenantContext tenantContext = httpContext.RequestServices.GetRequiredService<TenantContext>();

        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(httpContext.User, httpContext);
        int? userId = TenantClaimHelper.GetUserIdFromClaims(httpContext.User);
        tenantContext.Set(tenantId, userId);

        return Task.CompletedTask;
    }
}
