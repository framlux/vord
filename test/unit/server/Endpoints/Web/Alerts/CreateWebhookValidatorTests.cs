// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Services.Core.Models.Alerts;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Alerts;

/// <summary>
/// Unit tests for <see cref="CreateWebhookValidator"/>.
/// </summary>
public sealed class CreateWebhookValidatorTests
{
    private readonly CreateWebhookValidator _validator = new();

    private static CreateWebhookRequest ValidRequest()
    {
        return new CreateWebhookRequest
        {
            Name = "Production Alert Webhook",
            Url = "https://example.com/webhook/alerts"
        };
    }

    // ================================================================
    // Valid request
    // ================================================================

    [Test]
    public async Task ValidRequest_PassesValidation()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidRequest());

        await Assert.That(result.IsValid).IsTrue();
    }

    // ================================================================
    // Name validation
    // ================================================================

    [Test]
    public async Task EmptyName_FailsValidation()
    {
        CreateWebhookRequest request = ValidRequest();
        request.Name = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Webhook name is required")).IsTrue();
    }

    [Test]
    public async Task WhitespaceName_FailsValidation()
    {
        CreateWebhookRequest request = ValidRequest();
        request.Name = "   ";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task NameExactly250Characters_PassesValidation()
    {
        CreateWebhookRequest request = ValidRequest();
        request.Name = new string('A', 250);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameExceeds250Characters_FailsValidation()
    {
        CreateWebhookRequest request = ValidRequest();
        request.Name = new string('A', 251);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Webhook name must be 250 characters or fewer")).IsTrue();
    }

    // ================================================================
    // URL validation
    // ================================================================

    [Test]
    public async Task EmptyUrl_FailsValidation()
    {
        CreateWebhookRequest request = ValidRequest();
        request.Url = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Webhook URL is required")).IsTrue();
    }

    [Test]
    public async Task UrlExactly2000Characters_PassesValidation()
    {
        CreateWebhookRequest request = ValidRequest();
        request.Url = "https://example.com/" + new string('a', 1980);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task UrlExceeds2000Characters_FailsValidation()
    {
        CreateWebhookRequest request = ValidRequest();
        request.Url = "https://example.com/" + new string('a', 1981);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Webhook URL must be 2000 characters or fewer")).IsTrue();
    }

    [Test]
    public async Task HttpUrl_FailsValidation()
    {
        CreateWebhookRequest request = ValidRequest();
        request.Url = "http://example.com/webhook";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Webhook URL must be HTTPS and must not point to a private or reserved address")).IsTrue();
    }

    [Test]
    public async Task LocalhostUrl_FailsValidation()
    {
        CreateWebhookRequest request = ValidRequest();
        request.Url = "https://localhost/webhook";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Webhook URL must be HTTPS and must not point to a private or reserved address")).IsTrue();
    }

    [Test]
    public async Task PrivateIpUrl_FailsValidation()
    {
        CreateWebhookRequest request = ValidRequest();
        request.Url = "https://10.0.0.1/webhook";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Webhook URL must be HTTPS and must not point to a private or reserved address")).IsTrue();
    }

    [Test]
    public async Task ValidPublicHttpsUrl_PassesValidation()
    {
        CreateWebhookRequest request = ValidRequest();
        request.Url = "https://hooks.slack.com/services/T123/B456/xyz";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }
}
