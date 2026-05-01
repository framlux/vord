// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <inheritdoc/>
public sealed class DowngradeCleanupService : IDowngradeCleanupService
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IWebhookRepository _webhookRepo;
    private readonly ILogger<DowngradeCleanupService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="DowngradeCleanupService"/> class.
    /// </summary>
    public DowngradeCleanupService(
        ITenantRepository tenantRepo,
        IAlertRuleRepository alertRuleRepo,
        IWebhookRepository webhookRepo,
        ILogger<DowngradeCleanupService> logger)
    {
        _tenantRepo = tenantRepo;
        _alertRuleRepo = alertRuleRepo;
        _webhookRepo = webhookRepo;
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

        // Disable webhook notification endpoints
        int webhooksDisabled = await _webhookRepo.DisableWebhooksForTenantAsync(tenantId, ct);

        if (webhooksDisabled > 0)
        {
            _logger.LogInformation(
                "Disabled {Count} webhook endpoints for tenant {TenantId} during downgrade to Free",
                webhooksDisabled, tenantId);
        }
    }
}
