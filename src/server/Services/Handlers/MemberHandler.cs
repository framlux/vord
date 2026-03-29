// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles operations for managing tenant members.
/// </summary>
public sealed class MemberHandler : IMemberHandler
{
    private readonly IDatabaseCache _databaseCache;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="MemberHandler"/> class.
    /// </summary>
    /// <param name="databaseCache">The database cache service.</param>
    /// <param name="subscriptionService">The subscription service.</param>
    public MemberHandler(IDatabaseCache databaseCache, ISubscriptionService subscriptionService)
    {
        _databaseCache = databaseCache;
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<ApiResponse<object>>> RemoveAsync(int targetUserId, int? tenantId, int currentUserId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<ApiResponse<object>>.Error(401, ApiResponse<object>.Error("Unauthorized"));
        }

        if (targetUserId == currentUserId)
        {
            return ServiceResult<ApiResponse<object>>.Error(400, ApiResponse<object>.Error("You cannot remove yourself from the organization"));
        }

        bool removed = await _databaseCache.DisableUserTenantRoleAsync(targetUserId, tenantId.Value, currentUserId, ct);
        if (removed == false)
        {
            return ServiceResult<ApiResponse<object>>.NotFound();
        }

        await _databaseCache.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, currentUserId, null,
            AuditAction.MemberRemoved, AuditResourceType.User,
            targetUserId.ToString(), null, null), ct);

        return ServiceResult<ApiResponse<object>>.Ok(ApiResponse<object>.Ok(new { }, "Member removed"));
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<ApiResponse<object>>> ChangeRoleAsync(int targetUserId, int? tenantId, int currentUserId, string newRole, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<ApiResponse<object>>.Error(401, ApiResponse<object>.Error("Unauthorized"));
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier != SubscriptionTier.Team))
        {
            return ServiceResult<ApiResponse<object>>.Error(403, ApiResponse<object>.Error("Role management requires a Team subscription"));
        }

        if (string.IsNullOrEmpty(newRole) || Enum.TryParse<UserAccountRoles>(newRole, true, out UserAccountRoles parsedRole) == false)
        {
            return ServiceResult<ApiResponse<object>>.Error(400, ApiResponse<object>.Error("Invalid role specified"));
        }

        if (targetUserId == currentUserId)
        {
            return ServiceResult<ApiResponse<object>>.Error(400, ApiResponse<object>.Error("You cannot change your own role"));
        }

        bool disabled = await _databaseCache.DisableUserTenantRoleAsync(targetUserId, tenantId.Value, currentUserId, ct);
        if (disabled == false)
        {
            return ServiceResult<ApiResponse<object>>.NotFound();
        }

        await _databaseCache.CreateUserTenantRoleAsync(new UserTenantRole
        {
            UserId = targetUserId,
            AssignedTenantId = tenantId.Value,
            Role = parsedRole,
            AssignedByUserId = currentUserId,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        }, ct);

        await _databaseCache.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, currentUserId, null,
            AuditAction.MemberRoleChanged, AuditResourceType.User,
            targetUserId.ToString(), new { NewRole = newRole }, null), ct);

        return ServiceResult<ApiResponse<object>>.Ok(ApiResponse<object>.Ok(new { }, "Member role updated"));
    }
}
