// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Single source of truth for the tenant scope of the current request. Populated once per request
/// by <see cref="Framlux.FleetManagement.Server.Services.Tenancy.TenantContextPreProcessor"/> from
/// validated role claims and the vord_tenant cookie. Endpoints and handlers must read tenant scope
/// from here rather than re-deriving it from claims.
/// </summary>
public interface ITenantContext
{
    /// <summary>The resolved tenant ID for the current request, or null when no valid tenant scope exists.</summary>
    int? TenantId { get; }

    /// <summary>True when a valid tenant scope was resolved for the current request.</summary>
    bool HasTenant { get; }

    /// <summary>The authenticated user ID for the current request, or null when unauthenticated.</summary>
    int? UserId { get; }
}
