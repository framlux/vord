// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Users;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using LinqToDB.Async;
using LinqToDB;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles user management operations.
/// </summary>
public sealed class UserHandler : IUserHandler
{
    private readonly DatabaseContext _db;
    private readonly ILogger<UserHandler> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="UserHandler"/> class.
    /// </summary>
    public UserHandler(DatabaseContext db, ILogger<UserHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<List<UserAccountDto>>> ListAsync(int? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<List<UserAccountDto>>.Ok([]);
        }

        List<int> tenantUserIds = await _db.UserTenantRoles
            .Where(r => r.AssignedTenantId == tenantId.Value && r.IsActive)
            .Select(r => r.UserId)
            .Distinct()
            .ToListAsync(ct);

        List<UserAccount> users = await _db.UserAccounts
            .Where(u => u.IsSystem == false && tenantUserIds.Contains(u.Id))
            .OrderBy(u => u.Username)
            .ToListAsync(ct);

        List<UserTenantRole> tenantRoles = await _db.UserTenantRoles
            .LoadWith(r => r.AssignedTenant)
            .Where(r => r.IsActive && r.AssignedTenantId == tenantId.Value)
            .ToListAsync(ct);

        Dictionary<int, List<UserTenantRole>> rolesByUser = tenantRoles
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

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

        bool userInTenant = await _db.UserTenantRoles
            .AnyAsync(r => r.UserId == userId && r.AssignedTenantId == tenantId.Value && r.IsActive, ct);

        if (userInTenant == false)
        {
            return ServiceResult<UserAccountDto>.NotFound();
        }

        UserAccount? user = await _db.UserAccounts
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
        {
            return ServiceResult<UserAccountDto>.NotFound();
        }

        List<UserTenantRole> roles = await _db.UserTenantRoles
            .LoadWith(r => r.AssignedTenant)
            .Where(r => r.UserId == userId && r.AssignedTenantId == tenantId.Value && r.IsActive)
            .ToListAsync(ct);

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

        bool userInTenant = await _db.UserTenantRoles
            .AnyAsync(r => r.UserId == targetUserId && r.AssignedTenantId == tenantId.Value && r.IsActive, ct);

        if (userInTenant == false)
        {
            return ServiceResult<object>.NotFound();
        }

        int roleUpdated = await _db.UserTenantRoles
            .Where(r => r.UserId == targetUserId &&
                         r.AssignedTenantId == tenantId.Value &&
                         r.IsActive)
            .Set(r => r.IsActive, false)
            .Set(r => r.DisabledByUserId, currentUserId)
            .Set(r => r.DisabledAt, DateTimeOffset.UtcNow)
            .UpdateAsync(ct);

        if (roleUpdated == 0)
        {
            return ServiceResult<object>.NotFound();
        }

        bool hasActiveRoles = await _db.UserTenantRoles
            .AnyAsync(r => r.UserId == targetUserId && r.IsActive, ct);

        if (hasActiveRoles == false)
        {
            await _db.UserAccounts
                .Where(u => u.Id == targetUserId && u.IsActive && u.IsSystem == false)
                .Set(u => u.IsActive, false)
                .Set(u => u.DeletedOn, DateTimeOffset.UtcNow)
                .Set(u => u.DeletedByUserId, currentUserId)
                .UpdateAsync(ct);

            _logger.LogInformation("User {TargetUserId} fully deactivated (no active roles remain) by user {CurrentUserId}", targetUserId, currentUserId);
        }
        else
        {
            _logger.LogInformation("User {TargetUserId} removed from tenant {TenantId} by user {CurrentUserId}", targetUserId, tenantId.Value, currentUserId);
        }

        return ServiceResult<object>.Ok(new { });
    }
}
