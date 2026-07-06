// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Security;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <inheritdoc/>
public sealed class DowngradeCleanupService : IDowngradeCleanupService
{
    // Machine trims performed by an automated billing reversion are attributed to the system user.
    private const int SystemUserId = 1;

    private readonly ITenantRepository _tenantRepo;
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IIntegrationRepository _integrationRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly ITierFeatureLimitRepository _tierLimitRepo;
    private readonly IAuditLogRepository _auditLog;
    private readonly IApiKeyCacheInvalidator _apiKeyCacheInvalidator;
    private readonly ILogger<DowngradeCleanupService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="DowngradeCleanupService"/> class.
    /// </summary>
    public DowngradeCleanupService(
        ITenantRepository tenantRepo,
        IAlertRuleRepository alertRuleRepo,
        IIntegrationRepository integrationRepo,
        IMachineRepository machineRepo,
        ITierFeatureLimitRepository tierLimitRepo,
        IAuditLogRepository auditLog,
        IApiKeyCacheInvalidator apiKeyCacheInvalidator,
        ILogger<DowngradeCleanupService> logger)
    {
        ArgumentNullException.ThrowIfNull(tenantRepo);
        ArgumentNullException.ThrowIfNull(alertRuleRepo);
        ArgumentNullException.ThrowIfNull(integrationRepo);
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(tierLimitRepo);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(apiKeyCacheInvalidator);
        ArgumentNullException.ThrowIfNull(logger);

        _tenantRepo = tenantRepo;
        _alertRuleRepo = alertRuleRepo;
        _integrationRepo = integrationRepo;
        _machineRepo = machineRepo;
        _tierLimitRepo = tierLimitRepo;
        _auditLog = auditLog;
        _apiKeyCacheInvalidator = apiKeyCacheInvalidator;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task CleanupForProTierAsync(int tenantId, CancellationToken ct)
    {
        // Disable custom OIDC configuration
        int oidcDisabled = await _tenantRepo.DisableTenantOidcConfigAsync(tenantId, ct);

        if (oidcDisabled > 0)
        {
            _logger.LogInformation(
                "Disabled custom OIDC configuration for tenant {TenantId} during downgrade to Pro",
                tenantId);
        }

        // Disable custom alert rules (keep default/system rules active)
        int rulesDisabled = await _alertRuleRepo.DisableCustomAlertRulesForTenantAsync(tenantId, ct);

        if (rulesDisabled > 0)
        {
            _logger.LogInformation(
                "Disabled {Count} custom alert rules for tenant {TenantId} during downgrade to Pro",
                rulesDisabled, tenantId);
        }
    }

    /// <inheritdoc/>
    public async Task CleanupForFreeTierAsync(int tenantId, CancellationToken ct)
    {
        // Disable custom OIDC configuration
        await _tenantRepo.DisableTenantOidcConfigAsync(tenantId, ct);

        // Disable ALL alert rules for the tenant (Free tier has no alerting)
        int rulesDisabled = await _alertRuleRepo.DisableAlertRulesForTenantAsync(tenantId, customOnly: false, ct);

        if (rulesDisabled > 0)
        {
            _logger.LogInformation(
                "Disabled {Count} alert rules for tenant {TenantId} during downgrade to Free",
                rulesDisabled, tenantId);
        }

        // Disable integration notification endpoints
        int integrationsDisabled = await _integrationRepo.DisableIntegrationsForTenantAsync(tenantId, ct);

        if (integrationsDisabled > 0)
        {
            _logger.LogInformation(
                "Disabled {Count} integration endpoints for tenant {TenantId} during downgrade to Free",
                integrationsDisabled, tenantId);
        }

        await TrimMachinesToFreeLimitAsync(tenantId, ct);
    }

    /// <summary>
    /// Soft-deletes machines beyond the Free tier limit, keeping the oldest-registered. Reuses the
    /// soft-delete path (which returns the deleted key's hash so the API-key auth cache can be
    /// invalidated) and writes one audit entry per trimmed machine. At-or-under the limit is a no-op, so
    /// re-running this after an interactive downgrade that already ensured compliance is safe.
    /// </summary>
    private async Task TrimMachinesToFreeLimitAsync(int tenantId, CancellationToken ct)
    {
        TierFeatureLimit? freeLimits = await _tierLimitRepo.GetLimitsForTierAsync(SubscriptionTier.Free, ct);
        if (freeLimits is null)
        {
            _logger.LogWarning("Free tier limits not found; skipping machine trim for tenant {TenantId}", tenantId);

            return;
        }

        int freeMachineLimit = freeLimits.MachineLimit;

        List<Machine> machines = await _machineRepo.ListActiveMachinesForTenantAsync(tenantId, ct);
        if (machines.Count <= freeMachineLimit)
        {
            return;
        }

        // Keep the oldest-registered machines; trim the newest beyond the limit.
        List<Machine> toTrim = machines
            .OrderBy(m => m.RegisteredOn)
            .ThenBy(m => m.Id)
            .Skip(freeMachineLimit)
            .ToList();

        foreach (Machine machine in toTrim)
        {
            string? deletedKeyHash = await _machineRepo.SoftDeleteMachineAsync(machine.Id, tenantId, SystemUserId, ct);

            if (deletedKeyHash is not null)
            {
                // Stop the trimmed machine from authenticating immediately, not just on the auth-cache TTL.
                await _apiKeyCacheInvalidator.InvalidateByHashAsync(deletedKeyHash, ct);
            }

            await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
                tenantId, null, machine.Id,
                AuditAction.MachineDeleted, AuditResourceType.Machine,
                machine.Id.ToString(),
                new { Reason = "Soft-deleted: machine count exceeded the Free tier limit on subscription reversion" },
                null), ct);
        }

        _logger.LogInformation(
            "Trimmed {Count} machines beyond the Free tier limit ({Limit}) for tenant {TenantId} during downgrade",
            toTrim.Count, freeMachineLimit, tenantId);
    }
}
