// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Services.Core.Models.Machines;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Machines;

/// <summary>
/// Unit tests for <see cref="UpdateMachineValidator"/>.
/// </summary>
public sealed class UpdateMachineValidatorTests
{
    private readonly UpdateMachineValidator _validator = new();

    private static UpdateMachineRequest ValidRequest()
    {
        return new UpdateMachineRequest
        {
            Name = "web-server-01",
            Description = "Primary web server",
            Location = "US-East-1"
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
        UpdateMachineRequest request = ValidRequest();
        request.Name = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Name is required")).IsTrue();
    }

    [Test]
    public async Task WhitespaceName_FailsValidation()
    {
        UpdateMachineRequest request = ValidRequest();
        request.Name = "   ";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }

    [Test]
    public async Task NameExactly250Characters_PassesValidation()
    {
        UpdateMachineRequest request = ValidRequest();
        request.Name = new string('A', 250);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameExceeds250Characters_FailsValidation()
    {
        UpdateMachineRequest request = ValidRequest();
        request.Name = new string('A', 251);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Name must be 250 characters or fewer")).IsTrue();
    }

    // ================================================================
    // Location validation
    // ================================================================

    [Test]
    public async Task NullLocation_PassesValidation()
    {
        UpdateMachineRequest request = ValidRequest();
        request.Location = null;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task LocationExactly250Characters_PassesValidation()
    {
        UpdateMachineRequest request = ValidRequest();
        request.Location = new string('A', 250);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task LocationExceeds250Characters_FailsValidation()
    {
        UpdateMachineRequest request = ValidRequest();
        request.Location = new string('A', 251);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Location must be 250 characters or fewer")).IsTrue();
    }

    // ================================================================
    // Description validation (optional, no constraints in handler)
    // ================================================================

    [Test]
    public async Task NullDescription_PassesValidation()
    {
        UpdateMachineRequest request = ValidRequest();
        request.Description = null;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }
}
