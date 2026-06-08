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

    // ==========================================================================================
    // M9 regression: length caps and CRLF rejection.
    // ==========================================================================================

    [Test]
    public async Task NameAtMaxLength_PassesValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Name = new string('a', ContactFormValidator.MaxNameLength);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameOverMaxLength_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Name = new string('a', ContactFormValidator.MaxNameLength + 1);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task NameContainsLineFeed_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Name = "Bob\nFAKE LOG LINE";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task NameContainsCarriageReturn_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Name = "Bob\rFAKE LOG LINE";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task CompanyContainsLineFeed_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Company = "Acme\nInjected";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task FleetSizeContainsCarriageReturn_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.FleetSize = "100-500\rInjected";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task MessageOverMaxLength_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Message = new string('m', ContactFormValidator.MaxMessageLength + 1);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task MessageAtMaxLength_PassesValidation()
    {
        ContactFormRequest request = ValidRequest();
        request.Message = new string('m', ContactFormValidator.MaxMessageLength);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task EmailOverMaxLength_FailsValidation()
    {
        ContactFormRequest request = ValidRequest();
        // RFC 5321 max-length email; build something larger.
        string local = new('a', 250);
        request.Email = $"{local}@e.co"; // > 254 chars

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task ComputeEmailFingerprint_NormalizesCase()
    {
        string lower = ContactFormEndpoint.ComputeEmailFingerprint("user@EXAMPLE.com");
        string upper = ContactFormEndpoint.ComputeEmailFingerprint("USER@example.com");

        await Assert.That(lower).IsEqualTo(upper);
    }

    [Test]
    public async Task ComputeEmailFingerprint_StableShortHex()
    {
        string fp = ContactFormEndpoint.ComputeEmailFingerprint("jane@example.com");

        await Assert.That(fp.Length).IsEqualTo(16);
    }

    [Test]
    public async Task ComputeEmailFingerprint_DifferentEmails_DifferentFingerprints()
    {
        string a = ContactFormEndpoint.ComputeEmailFingerprint("a@x.com");
        string b = ContactFormEndpoint.ComputeEmailFingerprint("b@x.com");

        await Assert.That(a).IsNotEqualTo(b);
    }
}
