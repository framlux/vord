// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Models.Users;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles retrieval of the current authenticated user's data from the database.
/// </summary>
public sealed class AuthMeHandler : IAuthMeHandler
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

    /// <inheritdoc/>
    public async Task<ServiceResult<AuthMeResult>> GetCurrentUserAsync(string uniqueId, CancellationToken ct)
    {
        UserAccount? user = await _userRepository.GetUserByExternalIdAsync(uniqueId, ct);
        if (user is null)
        {
            return ServiceResult<AuthMeResult>.NotFound();
        }

        IEnumerable<UserTenantRole> tenants = await _tenantRepository.GetTenantsForUserAsync(uniqueId, ct);
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
