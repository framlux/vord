// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Admin;

/// <summary>
/// Validates the <see cref="UpdateAdminSettingsRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class UpdateAdminSettingsValidator : Validator<UpdateAdminSettingsRequest>
{
    /// <summary>
    /// Initializes validation rules for the update admin settings request.
    /// </summary>
    public UpdateAdminSettingsValidator()
    {
        RuleFor(x => x.Settings)
            .NotEmpty()
            .WithMessage("At least one setting is required");

        RuleForEach(x => x.Settings).ChildRules(entry =>
        {
            entry.RuleFor(e => e.Key)
                .GreaterThan(0)
                .WithMessage("Setting key must be a positive integer");

            entry.RuleFor(e => e.Value)
                .NotNull()
                .WithMessage("Setting value must not be null");
        });
    }
}
