// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.Admin;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Admin;

/// <summary>
/// Unit tests for <see cref="UpdateAdminSettingsValidator"/>.
/// </summary>
public sealed class UpdateAdminSettingsValidatorTests
{
    private readonly UpdateAdminSettingsValidator _validator = new();

    private static UpdateAdminSettingsRequest ValidRequest()
    {
        return new UpdateAdminSettingsRequest
        {
            Settings =
            [
                new SettingUpdateEntry { Key = 1, Value = "60" }
            ]
        };
    }

    [Test]
    public async Task ValidRequest_PassesValidation()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidRequest());

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task MultipleValidSettings_PassesValidation()
    {
        UpdateAdminSettingsRequest request = new()
        {
            Settings =
            [
                new SettingUpdateEntry { Key = 1, Value = "60" },
                new SettingUpdateEntry { Key = 2, Value = "300" },
                new SettingUpdateEntry { Key = 3, Value = "120" }
            ]
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    // ================================================================
    // Settings list validation
    // ================================================================

    [Test]
    public async Task EmptySettingsList_FailsValidation()
    {
        UpdateAdminSettingsRequest request = new() { Settings = [] };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "At least one setting is required")).IsTrue();
    }

    // ================================================================
    // Setting entry validation
    // ================================================================

    [Test]
    public async Task ZeroKey_FailsValidation()
    {
        UpdateAdminSettingsRequest request = new()
        {
            Settings = [new SettingUpdateEntry { Key = 0, Value = "60" }]
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Setting key must be a positive integer")).IsTrue();
    }

    [Test]
    public async Task NegativeKey_FailsValidation()
    {
        UpdateAdminSettingsRequest request = new()
        {
            Settings = [new SettingUpdateEntry { Key = -1, Value = "60" }]
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Setting key must be a positive integer")).IsTrue();
    }

    [Test]
    public async Task PositiveKey_PassesValidation()
    {
        UpdateAdminSettingsRequest request = new()
        {
            Settings = [new SettingUpdateEntry { Key = 1, Value = "60" }]
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }
}
