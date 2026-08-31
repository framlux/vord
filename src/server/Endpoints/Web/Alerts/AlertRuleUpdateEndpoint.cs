// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Request model for updating an alert rule.
/// </summary>
public sealed class UpdateAlertRuleRequest
{
    /// <summary>The rule name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The metric type of the rule, used for client-side validation of threshold and duration constraints.
    /// The handler verifies this matches the DB-stored metric (the metric is immutable after creation).
    /// </summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>The threshold value.</summary>
    public decimal Threshold { get; set; }

    /// <summary>Duration in minutes before firing.</summary>
    public int DurationMinutes { get; set; }

    /// <summary>The severity level.</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>Whether the rule is enabled.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Whether email notifications are enabled.</summary>
    public bool NotifyEmail { get; set; }

    /// <summary>Whether webhook notifications are enabled.</summary>
    public bool NotifyWebhook { get; set; }

    /// <summary>The machine IDs this rule should evaluate against.</summary>
    public long[] MachineIds { get; set; } = [];

    /// <summary>
    /// The machine IDs the caller was choosing from. Only assignments named here may be removed, so
    /// a caller that saw one page of a larger fleet cannot unassign the machines it never rendered.
    /// An empty array removes nothing.
    /// </summary>
    public long[] VisibleMachineIds { get; set; } = [];
}

/// <summary>
/// Updates an existing alert rule.
/// Requires TenantAdmin role and Pro+ subscription.
/// </summary>
public sealed class AlertRuleUpdateEndpoint : Endpoint<UpdateAlertRuleRequest, ApiResponse<AlertRuleDto>>
{
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IAlertRuleAssignmentService _assignmentService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAuditLogRepository _auditLog;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertRuleUpdateEndpoint"/> class.
    /// </summary>
    public AlertRuleUpdateEndpoint(
        IAlertRuleRepository alertRuleRepo,
        IAlertRuleAssignmentService assignmentService,
        ISubscriptionService subscriptionService,
        IAuditLogRepository auditLog,
        ITenantContext tenantContext)
    {
        _alertRuleRepo = alertRuleRepo;
        _assignmentService = assignmentService;
        _subscriptionService = subscriptionService;
        _auditLog = auditLog;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Put("/alert-rules/{id}");
        Policies(AuthorizationPolicies.TenantAdmin);
        Tags(Services.Billing.EndpointTags.RequiresProSubscription, EndpointTags.RequiresTenant);
        Options(b => b.WithMetadata(new RequiresProFeatureMessage(ProFeatureMessages.Alerting)));
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(UpdateAlertRuleRequest req, CancellationToken ct)
    {
        int tenantId = _tenantContext.RequireTenantId();

        // Pro+ gating (null/Free/non-Active → 403) is enforced by ProSubscriptionPreProcessor via
        // the RequiresProSubscription tag. The subscription is still loaded here for the custom-rule
        // Team-tier check below.
        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);

        int ruleId = Route<int>("id");

        AlertRule? rule = await _alertRuleRepo.GetAlertRuleByIdAsync(ruleId, tenantId, ct);

        if (rule is null)
        {
            await HttpContext.SendApiErrorAsync(404, "Alert rule not found", ct);

            return;
        }

        // Verify the metric in the request matches the DB-stored metric (metric is immutable)
        if (Enum.TryParse<AlertMetric>(req.Metric, true, out AlertMetric requestMetric) == false || requestMetric != rule.Metric)
        {
            await HttpContext.SendApiErrorAsync(400, "Metric in request does not match the rule's metric", ct);

            return;
        }

        if (rule.IsCustom && SubscriptionPolicy.RequiresTeam(subscription))
        {
            await HttpContext.SendApiErrorAsync(403, "Custom rules can only be modified with a Team subscription", ct);

            return;
        }

        if (Enum.TryParse<AlertSeverity>(req.Severity, true, out AlertSeverity severity) == false)
        {
            await HttpContext.SendApiErrorAsync(400, $"Invalid severity value: {req.Severity}", ct);

            return;
        }

        // A built-in rule below Team accepts an enable/disable and nothing else. Retuning a
        // threshold is authoring a rule, which is what the Team tier sells; without this, every
        // default is a custom rule wearing a different name.
        //
        // MachineIds is deliberately excluded from this comparison. An unassigned rule watches
        // nothing, so choosing which machines a built-in covers is the primary control Pro has over
        // it — scoping, not authoring. The metric needs no comparison either: the immutability check
        // above has already proven it equal.
        if ((rule.IsCustom == false) && SubscriptionPolicy.RequiresTeam(subscription))
        {
            // The edit form carries no description field and sends nothing, so a strict comparison
            // would permanently lock Pro out of toggling any built-in that acquired a description
            // while the tenant was on Team.
            bool descriptionUnchanged = string.IsNullOrEmpty(req.Description)
                ? string.IsNullOrEmpty(rule.Description)
                : string.Equals(req.Description, rule.Description, StringComparison.Ordinal);

            bool changesOnlyEnabled =
                (req.Name == rule.Name) &&
                descriptionUnchanged &&
                (req.Threshold == rule.Threshold) &&
                (req.DurationMinutes == rule.DurationMinutes) &&
                (severity == rule.Severity) &&
                (req.NotifyEmail == rule.NotifyEmail) &&
                (req.NotifyWebhook == rule.NotifyWebhook);

            if (changesOnlyEnabled == false)
            {
                await HttpContext.SendApiErrorAsync(403, "Built-in rules can be enabled or disabled on Pro. Editing them requires a Team subscription.", ct);

                return;
            }
        }

        // Validate threshold and duration based on the rule's metric type (requires DB-loaded metric)
        string? validationError = ValidateMetricConstraints(rule.Metric, req.Threshold, req.DurationMinutes);
        if (validationError is not null)
        {
            await HttpContext.SendApiErrorAsync(400, validationError, ct);

            return;
        }

        // Assignment goes through the same service the dedicated assignment endpoint and the
        // machine-side route use, so the tenant-ownership check on the machine ids and the bound on
        // what a partial view of the fleet may remove are stated once. It runs before the rule write
        // so an invalid machine id fails the whole request with the rule row untouched, matching the
        // create path's validate-first ordering.
        RuleMachineAssignmentResult assignment = await _assignmentService.SetMachinesForRuleAsync(
            rule, tenantId, req.MachineIds, req.VisibleMachineIds, subscription, ct);

        if (assignment.Outcome == AlertRuleAssignmentOutcome.InvalidMachineIds)
        {
            await HttpContext.SendApiErrorAsync(400, "One or more machine IDs are invalid or do not belong to this tenant", ct);

            return;
        }

        if (assignment.Outcome == AlertRuleAssignmentOutcome.CustomRuleRequiresTeam)
        {
            await HttpContext.SendApiErrorAsync(403, "Custom rules can only be modified with a Team subscription", ct);

            return;
        }

        if (assignment.Outcome == AlertRuleAssignmentOutcome.NotFound)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        await _alertRuleRepo.UpdateAlertRuleAsync(
            ruleId, tenantId,
            req.Name, req.Description,
            req.Threshold, req.DurationMinutes,
            severity, req.IsEnabled,
            req.NotifyEmail, req.NotifyWebhook, ct);

        int? userId = _tenantContext.UserId;
        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId, null,
            AuditAction.AlertRuleUpdated, AuditResourceType.AlertRule,
            ruleId.ToString(), req.Name, null), ct);

        AlertRuleDto dto = new()
        {
            Id = rule.Id,
            Name = req.Name,
            Description = req.Description,
            Metric = rule.Metric.ToString(),
            Operator = rule.Operator.ToString(),
            Threshold = req.Threshold,
            DurationMinutes = req.DurationMinutes,
            Severity = severity.ToString(),
            IsEnabled = req.IsEnabled,
            NotifyEmail = req.NotifyEmail,
            NotifyWebhook = req.NotifyWebhook,
            IsCustom = rule.IsCustom,
            MachineIds = [.. assignment.AppliedMachineIds],
        };

        await Send.OkAsync(ApiResponse<AlertRuleDto>.Ok(dto, "Alert rule updated"), cancellation: ct);
    }

    /// <summary>
    /// Validates threshold and duration constraints based on the metric type.
    /// Returns an error message if validation fails, or null if valid.
    /// </summary>
    internal static string? ValidateMetricConstraints(AlertMetric metric, decimal threshold, int durationMinutes)
    {
        if (AlertRuleMetricRules.ValidateThresholdForMetric(metric, threshold) == false)
        {
            return AlertRuleMetricRules.GetThresholdValidationMessage(metric);
        }

        if (AlertRuleMetricRules.ValidateDurationForMetric(metric, durationMinutes) == false)
        {
            return AlertRuleMetricRules.GetDurationValidationMessage(metric);
        }

        return null;
    }
}
