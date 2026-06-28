// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;
using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Validates the <see cref="CreateAlertRuleRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class CreateAlertRuleValidator : Validator<CreateAlertRuleRequest>
{
    /// <summary>
    /// Initializes validation rules for the create alert rule request.
    /// </summary>
    public CreateAlertRuleValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage("Rule name is required")
            .MaximumLength(250)
            .WithMessage("Rule name must be 250 characters or fewer");

        RuleFor(x => x.Description)
            .MaximumLength(2000)
            .WithMessage("Description must be 2000 characters or fewer")
            .When(x => x.Description is not null);

        RuleFor(x => x.DurationMinutes)
            .Must((req, duration) => AlertRuleMetricRules.ValidateDurationForMetric(req.Metric, duration))
            .WithMessage(req => AlertRuleMetricRules.GetDurationValidationMessage(req.Metric));

        RuleFor(x => x.Metric)
            .Must(metric => Enum.TryParse<AlertMetric>(metric, true, out _))
            .WithMessage("Invalid metric");

        RuleFor(x => x.Operator)
            .Must(op => Enum.TryParse<AlertOperator>(op, true, out _))
            .WithMessage("Invalid operator");

        RuleFor(x => x.Severity)
            .Must(sev => Enum.TryParse<AlertSeverity>(sev, true, out _))
            .WithMessage("Invalid severity");

        RuleFor(x => x.Threshold)
            .Must((req, threshold) => AlertRuleMetricRules.ValidateThresholdForMetric(req.Metric, threshold))
            .WithMessage(req => AlertRuleMetricRules.GetThresholdValidationMessage(req.Metric));

        RuleFor(x => x.MachineIds)
            .NotEmpty()
            .WithMessage("At least one machine must be selected");
    }
}
