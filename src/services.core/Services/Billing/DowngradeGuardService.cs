// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Validates whether a downgrade from Team tier is safe by ensuring at least one
/// TenantAdmin can still log in after custom OIDC is disabled.
/// </summary>
public sealed class DowngradeGuardService
{
    private readonly ITenantRepository _tenantRepository;

    /// <summary>
    /// Creates a new instance of the <see cref="DowngradeGuardService"/> class.
    /// </summary>
    public DowngradeGuardService(ITenantRepository tenantRepository)
    {
        ArgumentNullException.ThrowIfNull(tenantRepository);

        _tenantRepository = tenantRepository;
    }

    /// <summary>
    /// Checks whether a downgrade from Team tier is safe for the specified tenant.
    /// Returns true if at least one active TenantAdmin uses a social login provider.
    /// </summary>
    /// <param name="tenantId">The tenant ID to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the downgrade is safe; otherwise, false.</returns>
    public async Task<bool> CanDowngradeFromTeamAsync(int tenantId, CancellationToken ct)
    {
        bool hasNonOidcAdmin = await _tenantRepository.HasNonOidcTenantAdminAsync(tenantId, ct);

        return hasNonOidcAdmin;
    }
}
