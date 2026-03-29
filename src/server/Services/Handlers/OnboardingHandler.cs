// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles organization onboarding business logic.
/// </summary>
public sealed class OnboardingHandler : IOnboardingHandler
{
    private readonly IDatabaseCache _databaseCache;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="OnboardingHandler"/> class.
    /// </summary>
    /// <param name="databaseCache">The database cache service.</param>
    /// <param name="subscriptionService">The subscription service.</param>
    public OnboardingHandler(IDatabaseCache databaseCache, ISubscriptionService subscriptionService)
    {
        _databaseCache = databaseCache;
        _subscriptionService = subscriptionService;
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

        // Create Free subscription (Pro gets activated after Stripe checkout completes)
        await _subscriptionService.ProvisionFreeSubscriptionAsync(tenant.Id, ct);

        // Assign the creating user as TenantAdmin
        DateTimeOffset now = DateTimeOffset.UtcNow;
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

        return ServiceResult<OnboardingResult>.Ok(new OnboardingResult { TenantId = tenant.Id });
    }
}
