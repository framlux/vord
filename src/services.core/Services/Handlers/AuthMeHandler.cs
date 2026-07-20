// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Users;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles retrieval of the current authenticated user's data from the database.
/// </summary>
public sealed class AuthMeHandler
{
    private readonly IUserRepository _userRepository;
    private readonly ITenantRepository _tenantRepository;

    /// <summary>
    /// Creates a new instance of the <see cref="AuthMeHandler"/> class.
    /// </summary>
    /// <param name="userRepository">The user repository.</param>
    /// <param name="tenantRepository">The tenant repository.</param>
    public AuthMeHandler(IUserRepository userRepository, ITenantRepository tenantRepository)
    {
        ArgumentNullException.ThrowIfNull(userRepository);
        ArgumentNullException.ThrowIfNull(tenantRepository);

        _userRepository = userRepository;
        _tenantRepository = tenantRepository;
    }

    /// <summary>
    /// Retrieves the database-sourced user data for the specified external identity.
    /// </summary>
    /// <param name="authProvider">The authentication provider that issued the subject identifier.</param>
    /// <param name="uniqueId">The user's unique identifier from the identity provider.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the user's database-sourced data.</returns>
    public async Task<ServiceResult<AuthMeResult>> GetCurrentUserAsync(AuthProviderType authProvider, string uniqueId, CancellationToken ct)
    {
        UserAccount? user = await _userRepository.GetUserByExternalIdForProviderAsync(authProvider, uniqueId, ct);
        if (user is null)
        {
            return ServiceResult<AuthMeResult>.NotFound();
        }

        IEnumerable<UserTenantRole> tenants = await _tenantRepository.GetTenantsForUserByIdAsync(user.Id, ct);
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
