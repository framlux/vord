// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Services.Core.Models.Machines;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Machines;

/// <summary>
/// Unit tests for <see cref="CreateRegistrationTokenValidator"/>.
/// </summary>
public sealed class CreateRegistrationTokenValidatorTests
{
    private readonly CreateRegistrationTokenValidator _validator = new();

    [Test]
    public async Task ValidRequest_PassesValidation()
    {
        CreateRegistrationTokenRequest request = new() { Name = "Production Servers" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task EmptyName_FailsValidation()
    {
        CreateRegistrationTokenRequest request = new() { Name = string.Empty };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Token name is required")).IsTrue();
    }

    [Test]
    public async Task WhitespaceName_FailsValidation()
    {
        CreateRegistrationTokenRequest request = new() { Name = "   " };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Token name is required")).IsTrue();
    }

    [Test]
    public async Task NameExactly250Characters_PassesValidation()
    {
        CreateRegistrationTokenRequest request = new() { Name = new string('A', 250) };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameExceeds250Characters_FailsValidation()
    {
        CreateRegistrationTokenRequest request = new() { Name = new string('A', 251) };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Token name must be 250 characters or fewer")).IsTrue();
    }
}
