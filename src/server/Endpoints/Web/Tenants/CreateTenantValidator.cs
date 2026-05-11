// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.RegularExpressions;
using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Validates the <see cref="CreateTenantRequest"/> before the endpoint handler executes.
/// </summary>
public sealed partial class CreateTenantValidator : Validator<CreateTenantRequest>
{
    /// <summary>
    /// Initializes validation rules for the create tenant request.
    /// </summary>
    public CreateTenantValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage("Tenant name is required");

        RuleFor(x => x.Name)
            .Must(name => (name.Trim().Length >= 5) && (name.Trim().Length <= 100))
            .WithMessage("Tenant name must be between 5 and 100 characters")
            .When(x => string.IsNullOrWhiteSpace(x.Name) == false);

        RuleFor(x => x.Name)
            .Must(name => BlockedCharactersRegex().IsMatch(name) == false)
            .WithMessage("Tenant name contains characters that are not allowed")
            .When(x => string.IsNullOrWhiteSpace(x.Name) == false);
    }

    /// <summary>
    /// Matches characters that are not allowed in tenant names.
    /// Blocks HTML/injection characters and ASCII control characters.
    /// </summary>
    [GeneratedRegex("""[<>"'`\\/{}\|\x00-\x1F]""", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex BlockedCharactersRegex();
}
