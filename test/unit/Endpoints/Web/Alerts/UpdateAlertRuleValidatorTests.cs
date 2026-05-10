// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Alerts;

/// <summary>
/// Unit tests for <see cref="UpdateAlertRuleValidator"/>.
/// </summary>
public sealed class UpdateAlertRuleValidatorTests
{
    private readonly UpdateAlertRuleValidator _validator = new();

    private static UpdateAlertRuleRequest ValidRequest()
    {
        return new UpdateAlertRuleRequest
        {
            Name = "Updated Alert Rule",
            Description = "Updated description",
            Threshold = 80,
            DurationMinutes = 10,
            Severity = "Warning",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = [1L],
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
        UpdateAlertRuleRequest request = ValidRequest();
        request.Name = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Rule name is required")).IsTrue();
    }

    [Test]
    public async Task NameExceeds250Characters_FailsValidation()
    {
        UpdateAlertRuleRequest request = ValidRequest();
        request.Name = new string('A', 251);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Rule name must be 250 characters or fewer")).IsTrue();
    }

    [Test]
    public async Task NullDescription_PassesValidation()
    {
        UpdateAlertRuleRequest request = ValidRequest();
        request.Description = null;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task DescriptionExceeds2000Characters_FailsValidation()
    {
        UpdateAlertRuleRequest request = ValidRequest();
        request.Description = new string('A', 2001);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Description must be 2000 characters or fewer")).IsTrue();
    }

    [Test]
    public async Task NegativeDuration_FailsValidation()
    {
        UpdateAlertRuleRequest request = ValidRequest();
        request.DurationMinutes = -1;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Duration must be zero or positive")).IsTrue();
    }

    [Test]
    public async Task ZeroDuration_PassesValidation()
    {
        UpdateAlertRuleRequest request = ValidRequest();
        request.DurationMinutes = 0;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task InvalidSeverity_FailsValidation()
    {
        UpdateAlertRuleRequest request = ValidRequest();
        request.Severity = "Extreme";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Invalid severity")).IsTrue();
    }

    [Test]
    public async Task CaseInsensitiveSeverity_PassesValidation()
    {
        UpdateAlertRuleRequest request = ValidRequest();
        request.Severity = "critical";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NegativeThreshold_FailsValidation()
    {
        UpdateAlertRuleRequest request = ValidRequest();
        request.Threshold = -1;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Threshold must be zero or positive")).IsTrue();
    }

    [Test]
    public async Task ZeroThreshold_PassesValidation()
    {
        UpdateAlertRuleRequest request = ValidRequest();
        request.Threshold = 0;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task LargePositiveThreshold_PassesValidation()
    {
        UpdateAlertRuleRequest request = ValidRequest();
        request.Threshold = 999;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }
}
