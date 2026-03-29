// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Users;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles retrieval of the current authenticated user's data from the database.
/// </summary>
public sealed class AuthMeHandler : IAuthMeHandler
{
    private readonly IDatabaseCache _databaseCache;

    /// <summary>
    /// Creates a new instance of the <see cref="AuthMeHandler"/> class.
    /// </summary>
    /// <param name="databaseCache">The database cache service.</param>
    public AuthMeHandler(IDatabaseCache databaseCache)
    {
        _databaseCache = databaseCache;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<AuthMeResult>> GetCurrentUserAsync(string uniqueId, CancellationToken ct)
    {
        UserAccount? user = await _databaseCache.GetUserByExternalIdAsync(uniqueId, ct);
        if (user is null)
        {
            return ServiceResult<AuthMeResult>.NotFound();
        }

        IEnumerable<UserTenantRole> tenants = await _databaseCache.GetTenantsForUserAsync(uniqueId, ct);
        List<UserTenantDto> tenantDtos = tenants.Select(t => new UserTenantDto
        {
            TenantId = t.AssignedTenantId,
            TenantName = t.AssignedTenant?.Name ?? "Unknown",
            Role = ((int)t.Role).ToString(),
        }).ToList();

        AuthMeResult result = new()
        {
            UserId = user.Id,
            IsGlobalAdmin = user.IsGlobalAdmin,
            Tenants = tenantDtos,
            NeedsOnboarding = tenantDtos.Count == 0,
        };

        return ServiceResult<AuthMeResult>.Ok(result);
    }
}
