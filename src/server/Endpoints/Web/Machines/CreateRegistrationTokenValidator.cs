// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Validates the <see cref="CreateRegistrationTokenRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class CreateRegistrationTokenValidator : Validator<CreateRegistrationTokenRequest>
{
    /// <summary>
    /// Initializes validation rules for the create registration token request.
    /// </summary>
    public CreateRegistrationTokenValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage("Token name is required")
            .MaximumLength(250)
            .WithMessage("Token name must be 250 characters or fewer");
    }
}
