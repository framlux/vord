// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <inheritdoc/>
public sealed class DowngradeGuardService : IDowngradeGuardService
{
    private readonly DatabaseContext _db;

    /// <summary>
    /// Creates a new instance of the <see cref="DowngradeGuardService"/> class.
    /// </summary>
    public DowngradeGuardService(DatabaseContext db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public async Task<bool> CanDowngradeFromTeamAsync(int tenantId, CancellationToken ct)
    {
        // Check if at least one active TenantAdmin uses a social login provider (not CustomOidc)
        bool hasNonOidcAdmin = await (
            from utr in _db.UserTenantRoles
            join u in _db.UserAccounts on utr.UserId equals u.Id
            where utr.AssignedTenantId == tenantId &&
                  utr.Role == UserAccountRoles.TenantAdmin &&
                  utr.IsActive == true &&
                  u.IsActive == true &&
                  u.AuthProvider != AuthProviderType.CustomOidc
            select u.Id
        ).AnyAsync(ct);

        return hasNonOidcAdmin;
    }
}
