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

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles operations for managing tenant members.
/// </summary>
public sealed class MemberHandler : IMemberHandler
{
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly ITenantRepository _tenantRepository;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IRoleCacheInvalidator _roleCacheInvalidator;

    /// <summary>
    /// Creates a new instance of the <see cref="MemberHandler"/> class.
    /// </summary>
    /// <param name="transactionProvider">The database transaction provider.</param>
    /// <param name="auditLog">The audit log repository.</param>
    /// <param name="tenantRepository">The tenant repository.</param>
    /// <param name="subscriptionService">The subscription service.</param>
    /// <param name="roleCacheInvalidator">The role cache invalidator.</param>
    public MemberHandler(
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        ITenantRepository tenantRepository,
        ISubscriptionService subscriptionService,
        IRoleCacheInvalidator roleCacheInvalidator)
    {
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(tenantRepository);
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(roleCacheInvalidator);

        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _tenantRepository = tenantRepository;
        _subscriptionService = subscriptionService;
        _roleCacheInvalidator = roleCacheInvalidator;
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

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        bool removed = await _tenantRepository.DisableUserTenantRoleAsync(targetUserId, tenantId.Value, currentUserId, ct);
        if (removed == false)
        {
            return ServiceResult<ApiResponse<object>>.NotFound();
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, currentUserId, null,
            AuditAction.MemberRemoved, AuditResourceType.User,
            targetUserId.ToString(), null, null), ct);

        await transaction.CommitAsync(ct);

        // Invalidate the removed user's cached role claims after the transaction commits
        await _roleCacheInvalidator.InvalidateAsync(targetUserId, ct);

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

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

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

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, currentUserId, null,
            AuditAction.MemberRoleChanged, AuditResourceType.User,
            targetUserId.ToString(), new { NewRole = newRole }, null), ct);

        await transaction.CommitAsync(ct);

        // Invalidate the target user's cached role claims after the transaction commits
        await _roleCacheInvalidator.InvalidateAsync(targetUserId, ct);

        return ServiceResult<ApiResponse<object>>.Ok(ApiResponse<object>.Ok(new { }, "Member role updated"));
    }
}
