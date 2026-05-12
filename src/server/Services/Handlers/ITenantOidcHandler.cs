// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models.Tenants;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles tenant OIDC configuration operations.
/// </summary>
public interface ITenantOidcHandler
{
    /// <summary>
    /// Gets the OIDC configuration for a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="claimTenantId">The tenant ID from the user's claims.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the OIDC config DTO.</returns>
    Task<ServiceResult<TenantOidcConfigDto>> GetConfigAsync(int tenantId, int? claimTenantId, CancellationToken ct);

    /// <summary>
    /// Updates the OIDC configuration for a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="claimTenantId">The tenant ID from the user's claims.</param>
    /// <param name="request">The OIDC configuration to apply.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the updated OIDC config DTO.</returns>
    Task<ServiceResult<TenantOidcConfigDto>> UpdateConfigAsync(int tenantId, int? claimTenantId, TenantOidcConfigDto request, CancellationToken ct);
}
