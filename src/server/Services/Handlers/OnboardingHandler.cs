// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Security;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles organization onboarding business logic.
/// </summary>
public sealed class OnboardingHandler : IOnboardingHandler
{
    private readonly IDatabaseCache _databaseCache;
    private readonly SubscriptionOptions _subscriptionOptions;
    private readonly IRoleCacheInvalidator _roleCacheInvalidator;

    /// <summary>
    /// Creates a new instance of the <see cref="OnboardingHandler"/> class.
    /// </summary>
    /// <param name="databaseCache">The database cache service.</param>
    /// <param name="subscriptionOptions">The subscription tier configuration.</param>
    /// <param name="roleCacheInvalidator">The role cache invalidator.</param>
    public OnboardingHandler(
        IDatabaseCache databaseCache,
        IOptions<SubscriptionOptions> subscriptionOptions,
        IRoleCacheInvalidator roleCacheInvalidator)
    {
        _databaseCache = databaseCache;
        _subscriptionOptions = subscriptionOptions.Value;
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
        IEnumerable<UserTenantRole> existingRoles = await _databaseCache.GetTenantsForUserAsync(uniqueId, ct);
        if (existingRoles.Any())
        {
            return ServiceResult<OnboardingResult>.Error(409, new OnboardingResult { ErrorMessage = "You already belong to an organization" });
        }

        // Check if tenant name is taken
        Tenant? existing = await _databaseCache.GetTenantByNameAsync(organizationName, ct);
        if (existing is not null)
        {
            return ServiceResult<OnboardingResult>.Error(409, new OnboardingResult { ErrorMessage = "An organization with this name already exists" });
        }

        // Create tenant (unique constraint on name catches races)
        Tenant tenant;
        using IDatabaseTransaction transaction = await _databaseCache.BeginTransactionAsync(ct);

        try
        {
            tenant = await _databaseCache.CreateTenantAsync(new Tenant
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
            MachineLimit = _subscriptionOptions.FreeTierMachineLimit,
            RetentionDays = _subscriptionOptions.FreeTierRetentionDays,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _databaseCache.CreateTenantSubscriptionAsync(subscription, ct);

        // Assign the creating user as TenantAdmin
        await _databaseCache.CreateUserTenantRoleAsync(new UserTenantRole
        {
            UserId = userId,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = userId,
            AssignedAt = now,
            IsActive = true,
        }, ct);

        await _databaseCache.InsertAuditLogAsync(AuditHelper.Create(
            tenant.Id, userId, null,
            AuditAction.TenantCreated, AuditResourceType.Tenant,
            tenant.Id.ToString(), new { tenant.Name }, null), ct);

        await transaction.CommitAsync(ct);

        // Invalidate the cached role claims after the transaction commits
        await _roleCacheInvalidator.InvalidateAsync(userId, ct);

        return ServiceResult<OnboardingResult>.Ok(new OnboardingResult { TenantId = tenant.Id });
    }
}
