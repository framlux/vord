// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
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

    /// <inheritdoc/>
    public async Task<Dictionary<int, List<long>>> GetMachineIdsForRulesAsync(List<int> ruleIds, CancellationToken cancellationToken)
    {
        Dictionary<int, List<long>> result = ruleIds.ToDictionary(id => id, _ => new List<long>());

        if (ruleIds.Count == 0)
        {
            return result;
        }

        List<AlertRuleMachine> assignments = await _db.AlertRuleMachines
            .Where(arm => ruleIds.Contains(arm.AlertRuleId))
            .ToListAsync(cancellationToken);

        foreach (AlertRuleMachine assignment in assignments)
        {
            result[assignment.AlertRuleId].Add(assignment.MachineId);
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<List<long>> GetMachineIdsForRuleAsync(int ruleId, CancellationToken cancellationToken)
    {
        List<long> machineIds = await _db.AlertRuleMachines
            .Where(arm => arm.AlertRuleId == ruleId)
            .Select(arm => arm.MachineId)
            .ToListAsync(cancellationToken);

        return machineIds;
    }

    /// <inheritdoc/>
    public async Task SetMachinesForRuleAsync(int ruleId, IReadOnlyList<long> machineIds, CancellationToken cancellationToken)
    {
        await _db.AlertRuleMachines
            .Where(arm => arm.AlertRuleId == ruleId)
            .DeleteAsync(cancellationToken);

        List<long> distinctMachineIds = machineIds.Distinct().ToList();

        if (distinctMachineIds.Count > 0)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<AlertRuleMachine> assignments = distinctMachineIds
                .Select(machineId => new AlertRuleMachine
                {
                    AlertRuleId = ruleId,
                    MachineId = machineId,
                    CreatedAt = now
                })
                .ToList();

            await _db.BulkCopyAsync(new BulkCopyOptions { BulkCopyType = BulkCopyType.MultipleRows }, assignments, cancellationToken);
        }

        _logger.LogDebug(
            "Set {Count} machine assignments for alert rule {AlertRuleId}",
            distinctMachineIds.Count, ruleId);
    }

    /// <inheritdoc/>
    public async Task<List<int>> GetRuleIdsForMachineAsync(long machineId, int tenantId, CancellationToken cancellationToken)
    {
        List<int> ruleIds = await (from arm in _db.AlertRuleMachines
                                   join ar in _db.AlertRules on arm.AlertRuleId equals ar.Id
                                   where arm.MachineId == machineId && ar.TenantId == tenantId
                                   select arm.AlertRuleId)
            .ToListAsync(cancellationToken);

        return ruleIds;
    }

    /// <inheritdoc/>
    public async Task SetRulesForMachineAsync(long machineId, int tenantId, IReadOnlyList<int> ruleIds, CancellationToken cancellationToken)
    {
        // Get existing rule IDs for this machine within the tenant
        List<int> existingRuleIds = await GetRuleIdsForMachineAsync(machineId, tenantId, cancellationToken);

        // Delete existing assignments for this machine within the tenant
        if (existingRuleIds.Count > 0)
        {
            await _db.AlertRuleMachines
                .Where(arm => (arm.MachineId == machineId) && existingRuleIds.Contains(arm.AlertRuleId))
                .DeleteAsync(cancellationToken);
        }

        List<int> distinctRuleIds = ruleIds.Distinct().ToList();

        if (distinctRuleIds.Count > 0)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<AlertRuleMachine> assignments = distinctRuleIds
                .Select(ruleId => new AlertRuleMachine
                {
                    AlertRuleId = ruleId,
                    MachineId = machineId,
                    CreatedAt = now
                })
                .ToList();

            await _db.BulkCopyAsync(new BulkCopyOptions { BulkCopyType = BulkCopyType.MultipleRows }, assignments, cancellationToken);
        }

        _logger.LogDebug(
            "Set {Count} rule assignments for machine {MachineId} in tenant {TenantId}",
            distinctRuleIds.Count, machineId, tenantId);
    }

    /// <inheritdoc/>
    public async Task<int> RemoveAllMachineAssignmentsAsync(long machineId, CancellationToken cancellationToken)
    {
        int deleted = await _db.AlertRuleMachines
            .Where(arm => arm.MachineId == machineId)
            .DeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Removed {Count} alert rule machine assignments for machine {MachineId}",
                deleted, machineId);
        }

        return deleted;
    }
}
