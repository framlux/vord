// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;
using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

/// <summary>
/// Validates the <see cref="CreateIntegrationRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class CreateIntegrationValidator : Validator<CreateIntegrationRequest>
{
    /// <summary>
    /// Initializes validation rules for the create integration request.
    /// </summary>
    public CreateIntegrationValidator()
    {
        RuleFor(x => x.Provider)
            .NotEmpty()
            .WithMessage("Provider is required")
            .Must(provider => IsValidProvider(provider))
            .WithMessage("Invalid provider value");

        RuleFor(x => x.Name)
            .Length(1, 100)
            .WithMessage("Name must be between 1 and 100 characters")
            .When(x => string.IsNullOrWhiteSpace(x.Name) == false);
    }

    private static bool IsValidProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        // Validate that the string is a recognized enum value.
        // The handler enforces that None is not an acceptable value.
        return Enum.TryParse<IntegrationProvider>(provider, true, out _);
    }
}
