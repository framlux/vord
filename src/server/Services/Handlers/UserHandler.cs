// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Users;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles user management operations.
/// </summary>
public sealed class UserHandler : IUserHandler
{
    private readonly IUserRepository _userRepo;
    private readonly ILogger<UserHandler> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="UserHandler"/> class.
    /// </summary>
    public UserHandler(IUserRepository userRepo, ILogger<UserHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(userRepo);
        ArgumentNullException.ThrowIfNull(logger);

        _userRepo = userRepo;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<List<UserAccountDto>>> ListAsync(int? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<List<UserAccountDto>>.Ok([]);
        }

        (List<UserAccount> users, Dictionary<int, List<UserTenantRole>> rolesByUser) =
            await _userRepo.ListUsersForTenantAsync(tenantId.Value, ct);

        List<UserAccountDto> dtos = users.Select(u =>
        {
            List<UserTenantDto> tenants = new();
            if (rolesByUser.TryGetValue(u.Id, out List<UserTenantRole>? roles))
            {
                tenants = roles.Select(r => new UserTenantDto
                {
                    TenantId = r.AssignedTenantId,
                    TenantName = r.AssignedTenant?.Name ?? "Unknown",
                    Role = ((int)r.Role).ToString(),
                }).ToList();
            }

            return new UserAccountDto
            {
                Id = u.Id,
                Username = u.Username,
                IsActive = u.IsActive,
                IsGlobalAdmin = u.IsGlobalAdmin,
                CreatedAt = u.CreatedAt,
                Tenants = tenants,
            };
        }).ToList();

        return ServiceResult<List<UserAccountDto>>.Ok(dtos);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<UserAccountDto>> GetDetailAsync(int userId, int? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<UserAccountDto>.NotFound();
        }

        (UserAccount? user, List<UserTenantRole> roles) =
            await _userRepo.GetUserDetailForTenantAsync(userId, tenantId.Value, ct);

        if (user is null)
        {
            return ServiceResult<UserAccountDto>.NotFound();
        }

        UserAccountDto dto = new()
        {
            Id = user.Id,
            Username = user.Username,
            IsActive = user.IsActive,
            IsGlobalAdmin = user.IsGlobalAdmin,
            CreatedAt = user.CreatedAt,
            Tenants = roles.Select(r => new UserTenantDto
            {
                TenantId = r.AssignedTenantId,
                TenantName = r.AssignedTenant?.Name ?? "Unknown",
                Role = ((int)r.Role).ToString(),
            }).ToList(),
        };

        return ServiceResult<UserAccountDto>.Ok(dto);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<object>> DeactivateAsync(int targetUserId, int currentUserId, int? tenantId, CancellationToken ct)
    {
        if (targetUserId == currentUserId)
        {
            return ServiceResult<object>.Error(400, new { });
        }

        if (tenantId is null)
        {
            return ServiceResult<object>.NotFound();
        }

        // Check if the target user has an active role in this tenant
        (UserAccount? targetUser, List<UserTenantRole> targetRoles) =
            await _userRepo.GetUserDetailForTenantAsync(targetUserId, tenantId.Value, ct);

        if ((targetUser is null) || (targetRoles.Count == 0))
        {
            return ServiceResult<object>.NotFound();
        }

        int roleUpdated = await _userRepo.DeactivateUserTenantRolesAsync(targetUserId, tenantId.Value, currentUserId, ct);

        if (roleUpdated == 0)
        {
            return ServiceResult<object>.NotFound();
        }

        bool hasActiveRoles = await _userRepo.HasActiveRolesAsync(targetUserId, ct);

        if (hasActiveRoles == false)
        {
            await _userRepo.DeactivateUserAccountAsync(targetUserId, currentUserId, ct);

            _logger.LogInformation("User {TargetUserId} fully deactivated (no active roles remain) by user {CurrentUserId}", targetUserId, currentUserId);
        }
        else
        {
            _logger.LogInformation("User {TargetUserId} removed from tenant {TenantId} by user {CurrentUserId}", targetUserId, tenantId.Value, currentUserId);
        }

        return ServiceResult<object>.Ok(new { });
    }
}
