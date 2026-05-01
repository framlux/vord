// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <inheritdoc/>
public sealed class DowngradeGuardService : IDowngradeGuardService
{
    private readonly ITenantRepository _tenantRepository;

    /// <summary>
    /// Creates a new instance of the <see cref="DowngradeGuardService"/> class.
    /// </summary>
    public DowngradeGuardService(ITenantRepository tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }

    /// <inheritdoc/>
    public async Task<bool> CanDowngradeFromTeamAsync(int tenantId, CancellationToken ct)
    {
        bool hasNonOidcAdmin = await _tenantRepository.HasNonOidcTenantAdminAsync(tenantId, ct);

        return hasNonOidcAdmin;
    }
}
