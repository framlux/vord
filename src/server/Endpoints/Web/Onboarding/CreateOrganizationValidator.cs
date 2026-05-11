// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.RegularExpressions;
using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Onboarding;

/// <summary>
/// Validates the <see cref="CreateOrganizationRequest"/> before the endpoint handler executes.
/// </summary>
public sealed partial class CreateOrganizationValidator : Validator<CreateOrganizationRequest>
{
    /// <summary>
    /// Initializes validation rules for the create organization request.
    /// </summary>
    public CreateOrganizationValidator()
    {
        RuleFor(x => x.OrganizationName)
            .NotEmpty()
            .WithMessage("Organization name is required");

        RuleFor(x => x.OrganizationName)
            .Must(name => (name.Trim().Length >= 5) && (name.Trim().Length <= 100))
            .WithMessage("Organization name must be between 5 and 100 characters")
            .When(x => string.IsNullOrWhiteSpace(x.OrganizationName) == false);

        RuleFor(x => x.OrganizationName)
            .Must(name => BlockedCharactersRegex().IsMatch(name) == false)
            .WithMessage("Organization name contains characters that are not allowed")
            .When(x => string.IsNullOrWhiteSpace(x.OrganizationName) == false);
    }

    /// <summary>
    /// Matches characters that are not allowed in organization names.
    /// </summary>
    [GeneratedRegex("""[<>"'`\\/{}\|\x00-\x1F]""", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex BlockedCharactersRegex();
}
