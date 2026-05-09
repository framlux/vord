// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Alerts;

/// <summary>
/// Unit tests for <see cref="CreateAlertRuleValidator"/>.
/// </summary>
public sealed class CreateAlertRuleValidatorTests
{
    private readonly CreateAlertRuleValidator _validator = new();

    private static CreateAlertRuleRequest ValidRequest()
    {
        return new CreateAlertRuleRequest
        {
            Name = "High CPU Alert",
            Description = "Fires when CPU exceeds threshold",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Warning",
            NotifyEmail = true,
            NotifyWebhook = false,
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
        CreateAlertRuleRequest request = ValidRequest();
        request.Name = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Rule name is required")).IsTrue();
    }

    [Test]
    public async Task NameExactly250Characters_PassesValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Name = new string('A', 250);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameExceeds250Characters_FailsValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Name = new string('A', 251);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Rule name must be 250 characters or fewer")).IsTrue();
    }

    [Test]
    public async Task NullDescription_PassesValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Description = null;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task DescriptionExactly2000Characters_PassesValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Description = new string('A', 2000);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task DescriptionExceeds2000Characters_FailsValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Description = new string('A', 2001);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Description must be 2000 characters or fewer")).IsTrue();
    }

    [Test]
    public async Task NegativeDuration_FailsValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.DurationMinutes = -1;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Duration must be zero or positive")).IsTrue();
    }

    [Test]
    public async Task ZeroDuration_PassesValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.DurationMinutes = 0;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task InvalidMetric_FailsValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Metric = "InvalidMetric";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Invalid metric")).IsTrue();
    }

    [Test]
    public async Task CaseInsensitiveMetric_PassesValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Metric = "cpuusage";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task InvalidOperator_FailsValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Operator = "NotAnOperator";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Invalid operator")).IsTrue();
    }

    [Test]
    public async Task InvalidSeverity_FailsValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Severity = "Extreme";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Invalid severity")).IsTrue();
    }

    [Test]
    public async Task CaseInsensitiveSeverity_PassesValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Severity = "warning";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task PercentageMetric_ThresholdAbove100_FailsValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Metric = "CpuUsage";
        request.Threshold = 101;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Threshold for percentage metrics must be between 0 and 100")).IsTrue();
    }

    [Test]
    public async Task PercentageMetric_ThresholdNegative_FailsValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Metric = "MemoryUsage";
        request.Threshold = -1;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Threshold for percentage metrics must be between 0 and 100")).IsTrue();
    }

    [Test]
    public async Task PercentageMetric_Threshold50_PassesValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Metric = "DiskUsage";
        request.Threshold = 50;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task PercentageMetric_Threshold0_PassesValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Metric = "CpuUsage";
        request.Threshold = 0;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task PercentageMetric_Threshold100_PassesValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Metric = "CpuUsage";
        request.Threshold = 100;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task BinaryMetric_Threshold2_FailsValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Metric = "MachineOffline";
        request.Threshold = 2;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Threshold for this metric must be 0 or 1")).IsTrue();
    }

    [Test]
    public async Task BinaryMetric_Threshold0_PassesValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Metric = "MachineOffline";
        request.Threshold = 0;
        request.Operator = "EqualTo";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task BinaryMetric_Threshold1_PassesValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Metric = "DiskHealth";
        request.Threshold = 1;
        request.Operator = "EqualTo";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task OtherMetric_NegativeThreshold_FailsValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Metric = "FailedServices";
        request.Threshold = -1;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Threshold must be zero or positive")).IsTrue();
    }

    [Test]
    public async Task OtherMetric_ZeroThreshold_PassesValidation()
    {
        CreateAlertRuleRequest request = ValidRequest();
        request.Metric = "FailedServices";
        request.Threshold = 0;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }
}
