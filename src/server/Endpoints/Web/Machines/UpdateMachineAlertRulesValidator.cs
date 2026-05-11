// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Validates the <see cref="UpdateMachineAlertRulesRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class UpdateMachineAlertRulesValidator : Validator<UpdateMachineAlertRulesRequest>
{
    /// <summary>
    /// Initializes validation rules for the machine alert rules update request.
    /// </summary>
    public UpdateMachineAlertRulesValidator()
    {
        RuleFor(x => x.RuleIds)
            .NotNull()
            .WithMessage("Rule IDs array is required");

        RuleForEach(x => x.RuleIds)
            .GreaterThan(0)
            .WithMessage("Each rule ID must be a positive integer");
    }
}
