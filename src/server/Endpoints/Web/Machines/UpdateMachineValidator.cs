// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Validates the <see cref="UpdateMachineRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class UpdateMachineValidator : Validator<UpdateMachineRequest>
{
    /// <summary>
    /// Initializes validation rules for the update machine request.
    /// </summary>
    public UpdateMachineValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage("Name is required")
            .MaximumLength(250)
            .WithMessage("Name must be 250 characters or fewer");

        RuleFor(x => x.Location)
            .MaximumLength(250)
            .WithMessage("Location must be 250 characters or fewer")
            .When(x => x.Location is not null);
    }
}
