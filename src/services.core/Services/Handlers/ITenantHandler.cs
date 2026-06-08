// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Tenants;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles tenant management operations.
/// </summary>
public interface ITenantHandler
{
    /// <summary>
    /// Creates a new tenant.
    /// </summary>
    /// <param name="name">The tenant name.</param>
    /// <param name="logoUrl">The tenant logo URL.</param>
    /// <param name="userId">The ID of the creating user.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the created tenant DTO.</returns>
    Task<ServiceResult<TenantDto>> CreateAsync(string name, string logoUrl, int userId, CancellationToken ct);

    /// <summary>
    /// Gets the detail of a specific tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the tenant DTO.</returns>
    Task<ServiceResult<TenantDto>> GetDetailAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Lists tenants visible to a user.
    /// </summary>
    /// <param name="isGlobalAdmin">Whether the user is a global admin.</param>
    /// <param name="tenantIds">The tenant IDs the user has roles in.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the list of tenant DTOs.</returns>
    Task<ServiceResult<List<TenantDto>>> ListForUserAsync(bool isGlobalAdmin, List<int> tenantIds, CancellationToken ct);
}
