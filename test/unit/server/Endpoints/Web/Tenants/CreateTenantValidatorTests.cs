// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Services.Core.Models.Tenants;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Tenants;

/// <summary>
/// Unit tests for <see cref="CreateTenantValidator"/>.
/// </summary>
public sealed class CreateTenantValidatorTests
{
    private readonly CreateTenantValidator _validator = new();

    private static CreateTenantRequest ValidRequest()
    {
        return new CreateTenantRequest
        {
            Name = "Acme Corporation",
            LogoUrl = "https://example.com/logo.png"
        };
    }

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
        CreateTenantRequest request = ValidRequest();
        request.Name = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Tenant name is required")).IsTrue();
    }

    [Test]
    public async Task WhitespaceName_FailsValidation()
    {
        CreateTenantRequest request = ValidRequest();
        request.Name = "     ";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task NameExactly5Characters_PassesValidation()
    {
        CreateTenantRequest request = ValidRequest();
        request.Name = "ABCDE";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameBelow5Characters_FailsValidation()
    {
        CreateTenantRequest request = ValidRequest();
        request.Name = "ABCD";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Tenant name must be between 5 and 100 characters")).IsTrue();
    }

    [Test]
    public async Task NameExactly100Characters_PassesValidation()
    {
        CreateTenantRequest request = ValidRequest();
        request.Name = new string('A', 100);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameExceeds100Characters_FailsValidation()
    {
        CreateTenantRequest request = ValidRequest();
        request.Name = new string('A', 101);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Tenant name must be between 5 and 100 characters")).IsTrue();
    }

    // ================================================================
    // Blocked characters validation
    // ================================================================

    [Test]
    public async Task NameWithHtmlAngleBrackets_FailsValidation()
    {
        CreateTenantRequest request = ValidRequest();
        request.Name = "<script>alert(1)</script>";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Tenant name contains characters that are not allowed")).IsTrue();
    }

    [Test]
    public async Task NameWithBackslash_FailsValidation()
    {
        CreateTenantRequest request = ValidRequest();
        request.Name = "Acme\\Corp";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Tenant name contains characters that are not allowed")).IsTrue();
    }

    [Test]
    public async Task NameWithForwardSlash_FailsValidation()
    {
        CreateTenantRequest request = ValidRequest();
        request.Name = "Acme/Corp";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Tenant name contains characters that are not allowed")).IsTrue();
    }

    [Test]
    public async Task NameWithCurlyBraces_FailsValidation()
    {
        CreateTenantRequest request = ValidRequest();
        request.Name = "Acme{Corp}";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task NameWithPipe_FailsValidation()
    {
        CreateTenantRequest request = ValidRequest();
        request.Name = "Acme|Corp";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task NameWithHyphensAndSpaces_PassesValidation()
    {
        CreateTenantRequest request = ValidRequest();
        request.Name = "Acme Corp - East Division";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }
}
