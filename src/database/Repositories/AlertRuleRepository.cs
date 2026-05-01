// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IAlertRuleRepository
{
    /// <inheritdoc/>
    public async Task<List<AlertRule>> GetAlertRulesForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<AlertRule> rules = await _db.AlertRules
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

        return rules;
    }

    /// <inheritdoc/>
    public async Task<AlertRule> CreateAlertRuleAsync(AlertRule rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);

        rule.Id = await _db.InsertWithInt32IdentityAsync(rule, token: cancellationToken);

        _logger.LogDebug("Created alert rule {AlertRuleId} for tenant {TenantId}", rule.Id, rule.TenantId);

        return rule;
    }

    /// <inheritdoc/>
    public async Task<AlertRule?> GetAlertRuleByIdAsync(int ruleId, int tenantId, CancellationToken cancellationToken)
    {
        AlertRule? rule = await _db.AlertRules
            .FirstOrDefaultAsync(r => (r.Id == ruleId) && (r.TenantId == tenantId), cancellationToken);

        return rule;
    }

    /// <inheritdoc/>
    public async Task<AlertRule?> GetAlertRuleByIdAsync(int ruleId, CancellationToken cancellationToken)
    {
        AlertRule? rule = await _db.AlertRules
            .FirstOrDefaultAsync(r => r.Id == ruleId, cancellationToken);

        return rule;
    }

    /// <inheritdoc/>
    public async Task UpdateAlertRuleAsync(
        int ruleId,
        int tenantId,
        string name,
        string? description,
        decimal threshold,
        int durationMinutes,
        AlertSeverity severity,
        bool isEnabled,
        bool notifyEmail,
        bool notifyWebhook,
        CancellationToken cancellationToken)
    {
        await _db.AlertRules
            .Where(r => (r.Id == ruleId) && (r.TenantId == tenantId))
            .Set(r => r.Name, name)
            .Set(r => r.Description, description)
            .Set(r => r.Threshold, threshold)
            .Set(r => r.DurationMinutes, durationMinutes)
            .Set(r => r.Severity, severity)
            .Set(r => r.IsEnabled, isEnabled)
            .Set(r => r.NotifyEmail, notifyEmail)
            .Set(r => r.NotifyWebhook, notifyWebhook)
            .Set(r => r.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);

        _logger.LogDebug("Updated alert rule {AlertRuleId} for tenant {TenantId}", ruleId, tenantId);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteAlertRuleAsync(int ruleId, int tenantId, CancellationToken cancellationToken)
    {
        int deleted = await _db.AlertRules
            .Where(r => (r.Id == ruleId) && (r.TenantId == tenantId))
            .DeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted alert rule {AlertRuleId} for tenant {TenantId}", ruleId, tenantId);
        }

        return deleted;
    }

    /// <inheritdoc/>
    public async Task<List<AlertRule>> GetEnabledAlertRulesAsync(CancellationToken cancellationToken)
    {
        List<AlertRule> rules = await _db.AlertRules
            .Where(r => r.IsEnabled == true)
            .ToListAsync(cancellationToken);

        return rules;
    }

    /// <inheritdoc/>
    public async Task<int> CountAlertRulesForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        int count = await _db.AlertRules
            .Where(r => r.TenantId == tenantId)
            .CountAsync(cancellationToken);

        return count;
    }

    /// <inheritdoc/>
    public async Task<int> DisableAlertRulesForTenantAsync(int tenantId, bool customOnly, CancellationToken cancellationToken)
    {
        IQueryable<AlertRule> query = _db.AlertRules
            .Where(r => (r.TenantId == tenantId) && (r.IsEnabled == true));

        if (customOnly)
        {
            query = query.Where(r => r.IsCustom == true);
        }

        int updated = await query
            .Set(r => r.IsEnabled, false)
            .Set(r => r.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);

        if (updated > 0)
        {
            _logger.LogInformation(
                "Disabled {Count} alert rules for tenant {TenantId} (customOnly: {CustomOnly})",
                updated, tenantId, customOnly);
        }

        return updated;
    }

    /// <inheritdoc/>
    public async Task<int> DisableCustomAlertRulesForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        return await DisableAlertRulesForTenantAsync(tenantId, customOnly: true, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> HasDefaultAlertRulesAsync(int tenantId, CancellationToken cancellationToken)
    {
        bool hasRules = await _db.AlertRules
            .AnyAsync(r => (r.TenantId == tenantId) && (r.IsCustom == false), cancellationToken);

        return hasRules;
    }

    /// <inheritdoc/>
    public async Task InsertAlertRulesAsync(IEnumerable<AlertRule> rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (AlertRule rule in rules)
        {
            await _db.InsertAsync(rule, token: cancellationToken);
        }

        _logger.LogDebug("Inserted batch of alert rules");
    }
}
