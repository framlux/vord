// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for alert rule operations.
/// </summary>
public interface IAlertRuleRepository
{
    /// <summary>
    /// Returns all alert rules for a tenant, ordered by name.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<AlertRule>> GetAlertRulesForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new alert rule and sets its generated ID.
    /// </summary>
    /// <param name="rule">The alert rule to create.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<AlertRule> CreateAlertRuleAsync(AlertRule rule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an alert rule by ID scoped to a tenant, or null if not found.
    /// </summary>
    /// <param name="ruleId">The alert rule ID.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<AlertRule?> GetAlertRuleByIdAsync(int ruleId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an alert rule by ID without tenant scoping, or null if not found.
    /// Used by background workers that process rules across all tenants.
    /// </summary>
    /// <param name="ruleId">The alert rule ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<AlertRule?> GetAlertRuleByIdAsync(int ruleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates editable fields on an alert rule by ID within a tenant.
    /// </summary>
    /// <param name="ruleId">The alert rule ID.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="name">The updated rule name.</param>
    /// <param name="description">The updated description.</param>
    /// <param name="threshold">The updated threshold value.</param>
    /// <param name="durationMinutes">The updated duration in minutes.</param>
    /// <param name="severity">The updated severity level.</param>
    /// <param name="isEnabled">Whether the rule is enabled.</param>
    /// <param name="notifyEmail">Whether email notifications are enabled.</param>
    /// <param name="notifyWebhook">Whether webhook notifications are enabled.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task UpdateAlertRuleAsync(
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an alert rule by ID within a tenant. Returns the number of rows deleted.
    /// </summary>
    /// <param name="ruleId">The alert rule ID.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> DeleteAlertRuleAsync(int ruleId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all enabled alert rules across all tenants.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<AlertRule>> GetEnabledAlertRulesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of alert rules for a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> CountAlertRulesForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables alert rules for a tenant. When <paramref name="customOnly"/> is true, only custom rules
    /// are disabled; otherwise all enabled rules are disabled.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="customOnly">If true, only custom rules are disabled.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> DisableAlertRulesForTenantAsync(int tenantId, bool customOnly, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables only custom alert rules for a tenant. Convenience wrapper around
    /// <see cref="DisableAlertRulesForTenantAsync"/> with customOnly set to true.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> DisableCustomAlertRulesForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a tenant already has non-custom (default) alert rules provisioned.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<bool> HasDefaultAlertRulesAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts multiple alert rules in sequence.
    /// </summary>
    /// <param name="rules">The alert rules to insert.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertAlertRulesAsync(IEnumerable<AlertRule> rules, CancellationToken cancellationToken = default);
}
