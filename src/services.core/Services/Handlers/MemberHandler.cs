// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Services.Core.Security;
using System.Data;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles operations for managing tenant members.
/// </summary>
public sealed class MemberHandler
{
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly ITenantRepository _tenantRepository;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IRoleCacheInvalidator _roleCacheInvalidator;
    private readonly IUserSecurityStampService _securityStampService;

    /// <summary>
    /// Creates a new instance of the <see cref="MemberHandler"/> class.
    /// </summary>
    /// <param name="transactionProvider">The database transaction provider.</param>
    /// <param name="auditLog">The audit log repository.</param>
    /// <param name="tenantRepository">The tenant repository.</param>
    /// <param name="subscriptionService">The subscription service.</param>
    /// <param name="roleCacheInvalidator">The role cache invalidator.</param>
    /// <param name="securityStampService">The user security stamp service.</param>
    public MemberHandler(
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        ITenantRepository tenantRepository,
        ISubscriptionService subscriptionService,
        IRoleCacheInvalidator roleCacheInvalidator,
        IUserSecurityStampService securityStampService)
    {
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(tenantRepository);
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(roleCacheInvalidator);
        ArgumentNullException.ThrowIfNull(securityStampService);

        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _tenantRepository = tenantRepository;
        _subscriptionService = subscriptionService;
        _roleCacheInvalidator = roleCacheInvalidator;
        _securityStampService = securityStampService;
    }

    /// <summary>
    /// Removes a member from the specified tenant.
    /// </summary>
    /// <param name="targetUserId">The ID of the user to remove.</param>
    /// <param name="tenantId">The tenant ID, or null if not available.</param>
    /// <param name="currentUserId">The ID of the user performing the removal.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the API response.</returns>
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

        // The disable and the last-admin guard run in a single Serializable transaction with a bounded
        // 40001 retry so two concurrent admin removals cannot both observe the other still present and
        // both commit, orphaning the tenant.
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(IsolationLevel.Serializable, ct);

                UserAccountRoles? priorRole = await _tenantRepository.GetActiveUserRoleAsync(targetUserId, tenantId.Value, ct);
                if (priorRole is null)
                {
                    return ServiceResult<ApiResponse<object>>.NotFound();
                }

                bool removed = await _tenantRepository.DisableUserTenantRoleAsync(targetUserId, tenantId.Value, currentUserId, ct);
                if (removed == false)
                {
                    return ServiceResult<ApiResponse<object>>.NotFound();
                }

                // Only removing a TenantAdmin can strip the tenant of its last sign-in-capable admin, so
                // the guard is evaluated only for that case. Returning before the commit disposes the
                // transaction without committing, which rolls the disable back.
                if (priorRole == UserAccountRoles.TenantAdmin)
                {
                    bool hasAdminRemaining = await _tenantRepository.HasNonOidcTenantAdminAsync(tenantId.Value, ct);
                    if (hasAdminRemaining == false)
                    {
                        return ServiceResult<ApiResponse<object>>.Error(
                            409,
                            ApiResponse<object>.Error("Cannot remove the last administrator able to sign in without tenant SSO"));
                    }
                }

                await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
                    tenantId, currentUserId, null,
                    AuditAction.MemberRemoved, AuditResourceType.User,
                    targetUserId.ToString(), null, null), ct);

                await transaction.CommitAsync(ct);

                break;
            }
            catch (Exception ex) when (_transactionProvider.IsSerializationConflict(ex) && (attempt < maxAttempts))
            {
                // A committed concurrent removal aborted this one; retry against fresh state.
            }
        }

        // Invalidate the removed user's cached role claims, drop their cached privilege state, and
        // rotate their security stamp after the transaction commits so any existing cookie is
        // invalidated immediately.
        await _roleCacheInvalidator.InvalidateAsync(targetUserId, ct);
        await _roleCacheInvalidator.InvalidateUserStateAsync(targetUserId, ct);
        await _securityStampService.BumpAsync(targetUserId, ct);

        return ServiceResult<ApiResponse<object>>.Ok(ApiResponse<object>.Ok(new { }, "Member removed"));
    }

    /// <summary>
    /// Changes the role of a member in the specified tenant.
    /// </summary>
    /// <param name="targetUserId">The ID of the user whose role is being changed.</param>
    /// <param name="tenantId">The tenant ID, or null if not available.</param>
    /// <param name="currentUserId">The ID of the user performing the role change.</param>
    /// <param name="newRole">The new role to assign as a string.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the API response.</returns>
    public async Task<ServiceResult<ApiResponse<object>>> ChangeRoleAsync(int targetUserId, int? tenantId, int currentUserId, string newRole, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<ApiResponse<object>>.Error(401, ApiResponse<object>.Error("Unauthorized"));
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if (SubscriptionPolicy.RequiresTeam(subscription))
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

        // The two role writes and the last-admin guard run in a single Serializable transaction with a
        // bounded 40001 retry so concurrent demotions cannot both pass the guard and orphan the tenant.
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(IsolationLevel.Serializable, ct);

                UserAccountRoles? priorRole = await _tenantRepository.GetActiveUserRoleAsync(targetUserId, tenantId.Value, ct);
                if (priorRole is null)
                {
                    return ServiceResult<ApiResponse<object>>.NotFound();
                }

                bool disabled = await _tenantRepository.DisableUserTenantRoleAsync(targetUserId, tenantId.Value, currentUserId, ct);
                if (disabled == false)
                {
                    return ServiceResult<ApiResponse<object>>.NotFound();
                }

                await _tenantRepository.CreateUserTenantRoleAsync(new UserTenantRole
                {
                    UserId = targetUserId,
                    AssignedTenantId = tenantId.Value,
                    Role = parsedRole,
                    AssignedByUserId = currentUserId,
                    AssignedAt = DateTimeOffset.UtcNow,
                    IsActive = true,
                }, ct);

                // Only a demotion away from TenantAdmin can strip the tenant of its last sign-in-capable
                // admin, so the guard is evaluated only for that case. Returning before the commit disposes
                // the transaction without committing, rolling both writes back.
                if ((priorRole == UserAccountRoles.TenantAdmin) && (parsedRole != UserAccountRoles.TenantAdmin))
                {
                    bool hasAdminRemaining = await _tenantRepository.HasNonOidcTenantAdminAsync(tenantId.Value, ct);
                    if (hasAdminRemaining == false)
                    {
                        return ServiceResult<ApiResponse<object>>.Error(
                            409,
                            ApiResponse<object>.Error("Cannot change the role of the last administrator able to sign in without tenant SSO"));
                    }
                }

                await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
                    tenantId, currentUserId, null,
                    AuditAction.MemberRoleChanged, AuditResourceType.User,
                    targetUserId.ToString(), new { NewRole = newRole }, null), ct);

                await transaction.CommitAsync(ct);

                break;
            }
            catch (Exception ex) when (_transactionProvider.IsSerializationConflict(ex) && (attempt < maxAttempts))
            {
                // A committed concurrent role change aborted this one; retry against fresh state.
            }
        }

        // Invalidate the target user's cached role claims, drop their cached privilege state, and
        // rotate their security stamp after the transaction commits so any existing cookie is
        // invalidated immediately.
        await _roleCacheInvalidator.InvalidateAsync(targetUserId, ct);
        await _roleCacheInvalidator.InvalidateUserStateAsync(targetUserId, ct);
        await _securityStampService.BumpAsync(targetUserId, ct);

        return ServiceResult<ApiResponse<object>>.Ok(ApiResponse<object>.Ok(new { }, "Member role updated"));
    }
}
