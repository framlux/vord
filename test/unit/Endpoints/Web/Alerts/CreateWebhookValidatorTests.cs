// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

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
            Name = "Deploy Notifications",
            Url = "https://hooks.example.com/webhook",
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
        CreateWebhookRequest request = ValidRequest();
        request.Name = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Webhook name is required")).IsTrue();
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
        request.Url = "http://hooks.example.com/webhook";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Webhook URL must be HTTPS and must not point to a private or reserved address")).IsTrue();
    }

    [Test]
    public async Task BothFieldsEmpty_ReportsMultipleErrors()
    {
        CreateWebhookRequest request = new()
        {
            Name = string.Empty,
            Url = string.Empty,
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Count).IsGreaterThanOrEqualTo(2);
    }
}
