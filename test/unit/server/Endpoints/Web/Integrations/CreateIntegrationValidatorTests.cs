// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Services.Core.Models.Integrations;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Integrations;

/// <summary>
/// Unit tests for <see cref="CreateIntegrationValidator"/>.
/// </summary>
public sealed class CreateIntegrationValidatorTests
{
    private readonly CreateIntegrationValidator _validator = new();

    private static CreateIntegrationRequest ValidRequest()
    {
        return new CreateIntegrationRequest
        {
            Provider = "Slack",
            Name = "My Slack Integration",
            Configuration = new Dictionary<string, string>
            {
                ["webhookUrl"] = "https://hooks.slack.com/services/T00/B00/X"
            }
        };
    }

    [Test]
    public async Task ValidRequest_PassesValidation()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidRequest());

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NullName_PassesValidation()
    {
        CreateIntegrationRequest request = ValidRequest();
        request.Name = null;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    // ================================================================
    // Provider validation
    // ================================================================

    [Test]
    public async Task EmptyProvider_FailsValidation()
    {
        CreateIntegrationRequest request = ValidRequest();
        request.Provider = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Provider is required")).IsTrue();
    }

    [Test]
    public async Task InvalidProvider_FailsValidation()
    {
        CreateIntegrationRequest request = ValidRequest();
        request.Provider = "NotAProvider";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Invalid provider value")).IsTrue();
    }

    [Test]
    public async Task NoneProvider_PassesValidation_HandlerRejects()
    {
        // "None" is a valid enum value at the DTO level; the handler enforces the business rule
        CreateIntegrationRequest request = ValidRequest();
        request.Provider = "None";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task CaseInsensitiveProvider_PassesValidation()
    {
        CreateIntegrationRequest request = ValidRequest();
        request.Provider = "slack";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task AllValidProviders_PassValidation()
    {
        string[] validProviders = ["Slack", "MicrosoftTeams", "Discord", "PagerDuty", "Custom"];

        foreach (string provider in validProviders)
        {
            CreateIntegrationRequest request = ValidRequest();
            request.Provider = provider;

            ValidationResult result = await _validator.ValidateAsync(request);

            await Assert.That(result.IsValid).IsTrue();
        }
    }

    // ================================================================
    // Name validation
    // ================================================================

    [Test]
    public async Task NameExactly100Characters_PassesValidation()
    {
        CreateIntegrationRequest request = ValidRequest();
        request.Name = new string('A', 100);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameExceeds100Characters_FailsValidation()
    {
        CreateIntegrationRequest request = ValidRequest();
        request.Name = new string('A', 101);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Name must be between 1 and 100 characters")).IsTrue();
    }

    [Test]
    public async Task SingleCharacterName_PassesValidation()
    {
        CreateIntegrationRequest request = ValidRequest();
        request.Name = "A";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }
}
