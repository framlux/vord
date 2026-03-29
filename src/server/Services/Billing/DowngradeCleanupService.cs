// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using LinqToDB;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <inheritdoc/>
public sealed class DowngradeCleanupService : IDowngradeCleanupService
{
    private readonly DatabaseContext _db;
    private readonly ILogger<DowngradeCleanupService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="DowngradeCleanupService"/> class.
    /// </summary>
    public DowngradeCleanupService(DatabaseContext db, ILogger<DowngradeCleanupService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task CleanupForProTierAsync(int tenantId, CancellationToken ct)
    {
        // Disable custom OIDC configuration
        int oidcDisabled = await _db.TenantOidcConfigurations
            .Where(c => c.TenantId == tenantId && c.IsEnabled == true)
            .Set(c => c.IsEnabled, false)
            .Set(c => c.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(ct);

        if (oidcDisabled > 0)
        {
            _logger.LogInformation(
                "Disabled custom OIDC configuration for tenant {TenantId} during downgrade to Pro",
                tenantId);
        }

        // Disable custom alert rules (keep default/system rules active)
        int rulesDisabled = await _db.AlertRules
            .Where(r => r.TenantId == tenantId && r.IsCustom == true && r.IsEnabled == true)
            .Set(r => r.IsEnabled, false)
            .Set(r => r.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(ct);

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
        await _db.TenantOidcConfigurations
            .Where(c => c.TenantId == tenantId && c.IsEnabled == true)
            .Set(c => c.IsEnabled, false)
            .Set(c => c.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(ct);

        // Disable ALL alert rules for the tenant (Free tier has no alerting)
        int rulesDisabled = await _db.AlertRules
            .Where(r => r.TenantId == tenantId && r.IsEnabled == true)
            .Set(r => r.IsEnabled, false)
            .Set(r => r.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(ct);

        if (rulesDisabled > 0)
        {
            _logger.LogInformation(
                "Disabled {Count} alert rules for tenant {TenantId} during downgrade to Free",
                rulesDisabled, tenantId);
        }

        // Disable webhook notification endpoints
        int webhooksDisabled = await _db.WebhookEndpoints
            .Where(w => w.TenantId == tenantId && w.IsEnabled == true)
            .Set(w => w.IsEnabled, false)
            .UpdateAsync(ct);

        if (webhooksDisabled > 0)
        {
            _logger.LogInformation(
                "Disabled {Count} webhook endpoints for tenant {TenantId} during downgrade to Free",
                webhooksDisabled, tenantId);
        }
    }
}
