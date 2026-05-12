// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.Onboarding;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Onboarding;

/// <summary>
/// Unit tests for <see cref="CreateOrganizationValidator"/>.
/// </summary>
public sealed class CreateOrganizationValidatorTests
{
    private readonly CreateOrganizationValidator _validator = new();

    [Test]
    public async Task ValidName_PassesValidation()
    {
        CreateOrganizationRequest request = new() { OrganizationName = "Acme Corporation" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task EmptyName_FailsValidation()
    {
        CreateOrganizationRequest request = new() { OrganizationName = string.Empty };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Organization name is required")).IsTrue();
    }

    [Test]
    public async Task NameBelow5Characters_FailsValidation()
    {
        CreateOrganizationRequest request = new() { OrganizationName = "ABCD" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Organization name must be between 5 and 100 characters")).IsTrue();
    }

    [Test]
    public async Task NameExactly5Characters_PassesValidation()
    {
        CreateOrganizationRequest request = new() { OrganizationName = "ABCDE" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameExceeds100Characters_FailsValidation()
    {
        CreateOrganizationRequest request = new() { OrganizationName = new string('A', 101) };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task NameWithHtmlTags_FailsValidation()
    {
        CreateOrganizationRequest request = new() { OrganizationName = "<script>alert(1)</script>" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Organization name contains characters that are not allowed")).IsTrue();
    }

    [Test]
    public async Task NameWithHyphensAndSpaces_PassesValidation()
    {
        CreateOrganizationRequest request = new() { OrganizationName = "Acme Corp - West" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }
}
