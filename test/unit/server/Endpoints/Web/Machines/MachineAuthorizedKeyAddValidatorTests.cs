// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Services.Core.Models.Machines;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Machines;

/// <summary>
/// Unit tests for <see cref="MachineAuthorizedKeyAddValidator"/>.
/// </summary>
public sealed class MachineAuthorizedKeyAddValidatorTests
{
    private readonly MachineAuthorizedKeyAddValidator _validator = new();

    [Test]
    public async Task ValidSigningKeyId_PassesValidation()
    {
        MachineAuthorizedKeyAddRequest request = new() { SigningKeyId = 1 };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task ZeroSigningKeyId_FailsValidation()
    {
        MachineAuthorizedKeyAddRequest request = new() { SigningKeyId = 0 };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Signing key ID must be a positive integer")).IsTrue();
    }

    [Test]
    public async Task NegativeSigningKeyId_FailsValidation()
    {
        MachineAuthorizedKeyAddRequest request = new() { SigningKeyId = -1 };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }
}
