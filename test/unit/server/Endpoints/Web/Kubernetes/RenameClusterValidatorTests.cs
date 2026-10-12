// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.Kubernetes;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Kubernetes;

/// <summary>
/// Unit tests for <see cref="RenameClusterValidator"/>.
/// </summary>
public sealed class RenameClusterValidatorTests
{
    private readonly RenameClusterValidator _validator = new();

    [Test]
    public async Task NamedRequest_PassesValidation()
    {
        ValidationResult result = await _validator.ValidateAsync(new RenameClusterRequest { DisplayName = "Production" });

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NullName_PassesBecauseItResetsToTheDerivedName()
    {
        ValidationResult result = await _validator.ValidateAsync(new RenameClusterRequest { DisplayName = null });

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task EmptyOrWhitespaceName_PassesBecauseItResetsToTheDerivedName(string displayName)
    {
        ValidationResult result = await _validator.ValidateAsync(new RenameClusterRequest { DisplayName = displayName });

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameOfExactlyOneHundredCharacters_Passes()
    {
        ValidationResult result = await _validator.ValidateAsync(new RenameClusterRequest { DisplayName = new string('x', 100) });

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NameOfOneHundredAndOneCharacters_Fails()
    {
        ValidationResult result = await _validator.ValidateAsync(new RenameClusterRequest { DisplayName = new string('x', 101) });

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Display name must be 100 characters or fewer")).IsTrue();
    }

    [Test]
    public async Task OneHundredCharactersPaddedWithWhitespace_PassesBecauseTheLengthIsTrimmed()
    {
        string padded = "  " + new string('x', 100) + "  ";

        ValidationResult result = await _validator.ValidateAsync(new RenameClusterRequest { DisplayName = padded });

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task OneHundredAndOneCharactersPaddedWithWhitespace_Fails()
    {
        string padded = "  " + new string('x', 101) + "  ";

        ValidationResult result = await _validator.ValidateAsync(new RenameClusterRequest { DisplayName = padded });

        await Assert.That(result.IsValid).IsFalse();
    }
}
