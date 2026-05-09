// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.Billing;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Billing;

/// <summary>
/// Unit tests for <see cref="DowngradeSubscriptionValidator"/>.
/// </summary>
public sealed class DowngradeSubscriptionValidatorTests
{
    private readonly DowngradeSubscriptionValidator _validator = new();

    [Test]
    public async Task FreeTier_PassesValidation()
    {
        DowngradeSubscriptionRequest request = new() { TargetTier = "free" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task ProTier_PassesValidation()
    {
        DowngradeSubscriptionRequest request = new() { TargetTier = "pro" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task UppercaseFree_PassesValidation()
    {
        DowngradeSubscriptionRequest request = new() { TargetTier = "Free" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task UppercasePro_PassesValidation()
    {
        DowngradeSubscriptionRequest request = new() { TargetTier = "PRO" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task EmptyTargetTier_FailsValidation()
    {
        DowngradeSubscriptionRequest request = new() { TargetTier = string.Empty };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Target tier is required")).IsTrue();
    }

    [Test]
    public async Task InvalidTier_FailsValidation()
    {
        DowngradeSubscriptionRequest request = new() { TargetTier = "enterprise" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Target tier must be 'free' or 'pro'.")).IsTrue();
    }

    [Test]
    public async Task RandomString_FailsValidation()
    {
        DowngradeSubscriptionRequest request = new() { TargetTier = "abcdef" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Target tier must be 'free' or 'pro'.")).IsTrue();
    }

    [Test]
    public async Task TeamTier_FailsValidation()
    {
        DowngradeSubscriptionRequest request = new() { TargetTier = "team" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }
}
