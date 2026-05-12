// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.Alerts;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Validates the <see cref="UpdateAlertRuleRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class UpdateAlertRuleValidator : Validator<UpdateAlertRuleRequest>
{
    /// <summary>
    /// Initializes validation rules for the update alert rule request.
    /// </summary>
    public UpdateAlertRuleValidator()
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

        RuleFor(x => x.Metric)
            .Must(metric => Enum.TryParse<AlertMetric>(metric, true, out _))
            .WithMessage("Invalid metric");

        RuleFor(x => x.DurationMinutes)
            .Must((req, duration) => ValidateDurationForMetric(req.Metric, duration))
            .WithMessage(req => GetDurationValidationMessage(req.Metric));

        RuleFor(x => x.Severity)
            .Must(sev => Enum.TryParse<AlertSeverity>(sev, true, out _))
            .WithMessage("Invalid severity");

        RuleFor(x => x.Threshold)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Threshold must be zero or positive");

        RuleFor(x => x.MachineIds)
            .NotEmpty()
            .WithMessage("At least one machine must be selected");
    }

    private static bool ValidateDurationForMetric(string? metric, int duration)
    {
        if (Enum.TryParse<AlertMetric>(metric, true, out AlertMetric parsed) == false)
        {
            return duration >= 0;
        }

        if (AlertConstants.IsEventMetric(parsed))
        {
            return duration == 0;
        }

        return duration >= AlertConstants.GetMinimumDurationMinutes(parsed);
    }

    private static string GetDurationValidationMessage(string? metric)
    {
        if (Enum.TryParse<AlertMetric>(metric, true, out AlertMetric parsed) == false)
        {
            return "Duration must be zero or positive";
        }

        if (AlertConstants.IsEventMetric(parsed))
        {
            return "Duration must be zero for event-based metrics";
        }

        int minimum = AlertConstants.GetMinimumDurationMinutes(parsed);

        return $"Duration must be at least {minimum} minutes for {parsed} alerts";
    }
}
