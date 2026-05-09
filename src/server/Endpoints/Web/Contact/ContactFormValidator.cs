// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Contact;

/// <summary>
/// Validates the <see cref="ContactFormRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class ContactFormValidator : Validator<ContactFormRequest>
{
    /// <summary>
    /// Initializes validation rules for the contact form request.
    /// </summary>
    public ContactFormValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage("Name is required");

        RuleFor(x => x.Email)
            .NotEmpty()
            .WithMessage("Email is required")
            .EmailAddress()
            .WithMessage("A valid email address is required");

        RuleFor(x => x.Message)
            .NotEmpty()
            .WithMessage("Message is required");
    }
}
