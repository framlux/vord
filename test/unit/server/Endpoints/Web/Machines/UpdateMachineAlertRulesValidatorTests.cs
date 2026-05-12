// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Services.Core.Models.Machines;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Machines;

/// <summary>
/// Unit tests for <see cref="UpdateMachineAlertRulesValidator"/>.
/// </summary>
public sealed class UpdateMachineAlertRulesValidatorTests
{
    private readonly UpdateMachineAlertRulesValidator _validator = new();

    [Test]
    public async Task ValidRuleIds_PassesValidation()
    {
        UpdateMachineAlertRulesRequest request = new() { RuleIds = [1, 2, 3] };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task EmptyRuleIds_PassesValidation()
    {
        // Empty array is valid -- it means "clear all rule assignments"
        UpdateMachineAlertRulesRequest request = new() { RuleIds = [] };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task RuleIdZero_FailsValidation()
    {
        UpdateMachineAlertRulesRequest request = new() { RuleIds = [0] };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Each rule ID must be a positive integer")).IsTrue();
    }

    [Test]
    public async Task NegativeRuleId_FailsValidation()
    {
        UpdateMachineAlertRulesRequest request = new() { RuleIds = [1, -1, 3] };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task SingleValidRuleId_PassesValidation()
    {
        UpdateMachineAlertRulesRequest request = new() { RuleIds = [42] };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }
}
