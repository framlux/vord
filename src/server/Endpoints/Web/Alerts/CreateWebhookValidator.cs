// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;
using Framlux.FleetManagement.Server.Auth;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Validates the <see cref="CreateWebhookRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class CreateWebhookValidator : Validator<CreateWebhookRequest>
{
    /// <summary>
    /// Initializes validation rules for the create webhook request.
    /// </summary>
    public CreateWebhookValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage("Webhook name is required")
            .MaximumLength(250)
            .WithMessage("Webhook name must be 250 characters or fewer");

        RuleFor(x => x.Url)
            .NotEmpty()
            .WithMessage("Webhook URL is required");

        RuleFor(x => x.Url)
            .MaximumLength(2000)
            .WithMessage("Webhook URL must be 2000 characters or fewer")
            .Must(url => SsoOidcEvents.IsUrlSafe(url))
            .WithMessage("Webhook URL must be HTTPS and must not point to a private or reserved address")
            .When(x => string.IsNullOrWhiteSpace(x.Url) == false);
    }
}
