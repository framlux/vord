// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Contact;

/// <summary>
/// Validates the <see cref="ContactFormRequest"/> before the endpoint handler executes.
/// Caps every field to a sensible upper bound (Kestrel's 1 MB request limit is way too
/// loose for a contact form) and rejects CR/LF in single-line fields so a malicious submitter
/// cannot inject a fake structured-log line via the handler's logger.
/// </summary>
public sealed class ContactFormValidator : Validator<ContactFormRequest>
{
    /// <summary>RFC 5321 maximum email length.</summary>
    public const int MaxEmailLength = 254;

    /// <summary>Reasonable upper bound for a personal/business name.</summary>
    public const int MaxNameLength = 120;

    /// <summary>Reasonable upper bound for a company name.</summary>
    public const int MaxCompanyLength = 120;

    /// <summary>Free-text fleet-size indicator (e.g., "100-500"); short string.</summary>
    public const int MaxFleetSizeLength = 80;

    /// <summary>Cap for the message body. 5000 chars covers any realistic inquiry.</summary>
    public const int MaxMessageLength = 5000;

    /// <summary>Initializes validation rules for the contact form request.</summary>
    public ContactFormValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
                .WithMessage("Name is required")
            .MaximumLength(MaxNameLength)
                .WithMessage($"Name must be {MaxNameLength} characters or fewer")
            .Matches(@"^[^\r\n]+$")
                .WithMessage("Name must not contain newline characters");

        RuleFor(x => x.Email)
            .NotEmpty()
                .WithMessage("Email is required")
            .EmailAddress()
                .WithMessage("A valid email address is required")
            .MaximumLength(MaxEmailLength)
                .WithMessage($"Email must be {MaxEmailLength} characters or fewer");

        RuleFor(x => x.Company)
            .MaximumLength(MaxCompanyLength)
                .WithMessage($"Company must be {MaxCompanyLength} characters or fewer")
            .Matches(@"^[^\r\n]*$")
                .WithMessage("Company must not contain newline characters");

        RuleFor(x => x.FleetSize)
            .MaximumLength(MaxFleetSizeLength)
                .WithMessage($"Fleet size must be {MaxFleetSizeLength} characters or fewer")
            .Matches(@"^[^\r\n]*$")
                .WithMessage("Fleet size must not contain newline characters");

        RuleFor(x => x.Message)
            .NotEmpty()
                .WithMessage("Message is required")
            .MaximumLength(MaxMessageLength)
                .WithMessage($"Message must be {MaxMessageLength} characters or fewer");
    }
}
