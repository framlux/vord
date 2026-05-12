// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.Contact;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Contact;

/// <summary>
/// Unit tests for <see cref="ContactFormValidator"/>.
/// </summary>
public sealed class ContactFormValidatorTests
{
    private readonly ContactFormValidator _validator = new();

    private static ContactFormRequest ValidRequest()
    {
        return new ContactFormRequest
        {
            Name = "Jane Doe",
            Email = "jane@example.com",
            Message = "I am interested in the Team plan.",
        };
    }

    [Test]
    public async Task ValidRequest_PassesValidation()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidRequest());

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task EmptyName_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Name = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Name is required")).IsTrue();
    }

    [Test]
    public async Task WhitespaceName_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Name = "   ";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Name is required")).IsTrue();
    }

    [Test]
    public async Task EmptyEmail_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Email = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Email is required")).IsTrue();
    }

    [Test]
    public async Task InvalidEmailFormat_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Email = "not-an-email";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "A valid email address is required")).IsTrue();
    }

    [Test]
    public async Task EmptyMessage_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Message = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Message is required")).IsTrue();
    }

    [Test]
    public async Task WhitespaceMessage_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Message = "  \t  ";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Message is required")).IsTrue();
    }

    [Test]
    public async Task AllFieldsEmpty_ReportsMultipleErrors()
    {
        ContactFormRequest request = new()
        {
            Name = string.Empty,
            Email = string.Empty,
            Message = string.Empty,
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Count).IsGreaterThanOrEqualTo(3);
    }
}
