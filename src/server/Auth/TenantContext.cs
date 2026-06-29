// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Scoped, mutable holder for the current request's resolved tenant and user. Only the populating
/// pre-processor calls <see cref="Set"/>; consumers depend on the read-only <see cref="ITenantContext"/>.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    /// <inheritdoc/>
    public int? TenantId { get; private set; }

    /// <inheritdoc/>
    public bool HasTenant => TenantId is not null;

    /// <inheritdoc/>
    public int? UserId { get; private set; }

    /// <summary>
    /// Records the resolved tenant and user for the current request. Called once by the tenant
    /// context pre-processor.
    /// </summary>
    /// <param name="tenantId">The resolved tenant ID, or null when no valid scope exists.</param>
    /// <param name="userId">The authenticated user ID, or null when unauthenticated.</param>
    public void Set(int? tenantId, int? userId)
    {
        TenantId = tenantId;
        UserId = userId;
    }
}
