// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Security;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles organization onboarding business logic.
/// </summary>
public sealed class OnboardingHandler : IOnboardingHandler
{
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly ITenantRepository _tenantRepository;
    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly IAuditLogRepository _auditLog;
    private readonly IRoleCacheInvalidator _roleCacheInvalidator;

    /// <summary>
    /// Creates a new instance of the <see cref="OnboardingHandler"/> class.
    /// </summary>
    /// <param name="transactionProvider">The database transaction provider.</param>
    /// <param name="tenantRepository">The tenant repository.</param>
    /// <param name="subscriptionRepository">The subscription repository.</param>
    /// <param name="auditLog">The audit log repository.</param>
    /// <param name="roleCacheInvalidator">The role cache invalidator.</param>
    public OnboardingHandler(
        IDatabaseTransactionProvider transactionProvider,
        ITenantRepository tenantRepository,
        ISubscriptionRepository subscriptionRepository,
        IAuditLogRepository auditLog,
        IRoleCacheInvalidator roleCacheInvalidator)
    {
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(tenantRepository);
        ArgumentNullException.ThrowIfNull(subscriptionRepository);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(roleCacheInvalidator);

        _transactionProvider = transactionProvider;
        _tenantRepository = tenantRepository;
        _subscriptionRepository = subscriptionRepository;
        _auditLog = auditLog;
        _roleCacheInvalidator = roleCacheInvalidator;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<OnboardingResult>> CreateOrganizationAsync(
        string organizationName,
        string tier,
        int userId,
        string uniqueId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(organizationName) || organizationName.Length > 100)
        {
            return ServiceResult<OnboardingResult>.Error(400, new OnboardingResult { ErrorMessage = "Organization name is required (max 100 characters)" });
        }

        if (userId == 0)
        {
            return ServiceResult<OnboardingResult>.Error(401, new OnboardingResult { ErrorMessage = "Unauthorized" });
        }

        if (string.IsNullOrEmpty(uniqueId))
        {
            return ServiceResult<OnboardingResult>.Error(401, new OnboardingResult { ErrorMessage = "Unauthorized" });
        }

        // Check if user already has tenants
        IEnumerable<UserTenantRole> existingRoles = await _tenantRepository.GetTenantsForUserAsync(uniqueId, ct);
        if (existingRoles.Any())
        {
            return ServiceResult<OnboardingResult>.Error(409, new OnboardingResult { ErrorMessage = "You already belong to an organization" });
        }

        // Check if tenant name is taken
        Tenant? existing = await _tenantRepository.GetTenantByNameAsync(organizationName, ct);
        if (existing is not null)
        {
            return ServiceResult<OnboardingResult>.Error(409, new OnboardingResult { ErrorMessage = "An organization with this name already exists" });
        }

        // Create tenant (unique constraint on name catches races)
        Tenant tenant;
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        try
        {
            tenant = await _tenantRepository.CreateTenantAsync(new Tenant
            {
                Name = organizationName.Trim(),
                ExternalId = Guid.NewGuid().ToString(),
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = userId,
                IsActive = true,
                LogoUrl = string.Empty,
            }, ct);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return ServiceResult<OnboardingResult>.Error(409, new OnboardingResult { ErrorMessage = "An organization with this name already exists" });
        }

        // Create the free subscription within the same transaction so the FK on TenantId
        // can see the uncommitted Tenant row.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _subscriptionRepository.CreateTenantSubscriptionAsync(subscription, ct);

        // Assign the creating user as TenantAdmin
        await _tenantRepository.CreateUserTenantRoleAsync(new UserTenantRole
        {
            UserId = userId,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = userId,
            AssignedAt = now,
            IsActive = true,
        }, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenant.Id, userId, null,
            AuditAction.TenantCreated, AuditResourceType.Tenant,
            tenant.Id.ToString(), new { tenant.Name }, null), ct);

        await transaction.CommitAsync(ct);

        // Invalidate the cached role claims after the transaction commits
        await _roleCacheInvalidator.InvalidateAsync(userId, ct);

        return ServiceResult<OnboardingResult>.Ok(new OnboardingResult { TenantId = tenant.Id });
    }
}
