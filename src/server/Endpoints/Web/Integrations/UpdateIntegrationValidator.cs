// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

/// <summary>
/// Validates the <see cref="UpdateIntegrationRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class UpdateIntegrationValidator : Validator<UpdateIntegrationRequest>
{
    /// <summary>
    /// Initializes validation rules for the update integration request.
    /// </summary>
    public UpdateIntegrationValidator()
    {
        RuleFor(x => x.Name)
            .Must(name => (name!.Trim().Length >= 1) && (name.Trim().Length <= 100))
            .WithMessage("Name must be between 1 and 100 characters")
            .When(x => x.Name is not null);
    }
}
