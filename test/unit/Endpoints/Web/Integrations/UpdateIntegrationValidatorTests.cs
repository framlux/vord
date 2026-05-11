// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Integrations;

/// <summary>
/// Unit tests for <see cref="UpdateIntegrationValidator"/>.
/// </summary>
public sealed class UpdateIntegrationValidatorTests
{
    private readonly UpdateIntegrationValidator _validator = new();

    [Test]
    public async Task AllNullFields_PassesValidation()
    {
        UpdateIntegrationRequest request = new();

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task ValidName_PassesValidation()
    {
        UpdateIntegrationRequest request = new() { Name = "Updated Name" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameExactly100Characters_PassesValidation()
    {
        UpdateIntegrationRequest request = new() { Name = new string('A', 100) };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameExceeds100Characters_FailsValidation()
    {
        UpdateIntegrationRequest request = new() { Name = new string('A', 101) };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Name must be between 1 and 100 characters")).IsTrue();
    }

    [Test]
    public async Task WhitespaceOnlyName_FailsValidation()
    {
        UpdateIntegrationRequest request = new() { Name = "   " };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task SingleCharacterName_PassesValidation()
    {
        UpdateIntegrationRequest request = new() { Name = "A" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NullName_PassesValidation()
    {
        UpdateIntegrationRequest request = new() { Name = null };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }
}
