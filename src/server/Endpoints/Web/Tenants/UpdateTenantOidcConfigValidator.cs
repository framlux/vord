// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Validates the <see cref="TenantOidcConfigDto"/> before the endpoint handler executes.
/// </summary>
public sealed class UpdateTenantOidcConfigValidator : Validator<TenantOidcConfigDto>
{
    /// <summary>
    /// Initializes validation rules for the OIDC configuration request.
    /// </summary>
    public UpdateTenantOidcConfigValidator()
    {
        RuleFor(x => x.Authority)
            .NotEmpty()
            .WithMessage("Authority URL is required");

        RuleFor(x => x.Authority)
            .Must(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Authority URL must use HTTPS")
            .When(x => string.IsNullOrWhiteSpace(x.Authority) == false);

        RuleFor(x => x.ClientId)
            .NotEmpty()
            .WithMessage("Client ID is required");

        RuleFor(x => x.ClientSecret)
            .NotEmpty()
            .WithMessage("Client secret is required");

        RuleFor(x => x.EmailDomain)
            .NotEmpty()
            .WithMessage("Email domain is required");

        RuleFor(x => x.MetadataAddress)
            .Must(url => url!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Metadata address must use HTTPS")
            .When(x => string.IsNullOrEmpty(x.MetadataAddress) == false);
    }
}
