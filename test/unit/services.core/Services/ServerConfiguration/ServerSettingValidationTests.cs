// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="ServerSettingValidation"/>, focused on the Stripe canary setting keys.
/// </summary>
public class ServerSettingValidationTests
{
    [Test]
    public async Task Validate_StripeCanaryEnabled_RejectsNonBool()
    {
        string? error = ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryEnabled, "maybe");

        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Validate_StripeCanaryEnabled_AcceptsTrueAndFalse()
    {
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryEnabled, "true")).IsNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryEnabled, "false")).IsNull();
    }

    [Test]
    public async Task Validate_StripeCanaryIntervalSeconds_EnforcesBounds()
    {
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryIntervalSeconds, "29")).IsNotNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryIntervalSeconds, "3601")).IsNotNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryIntervalSeconds, "60")).IsNull();
    }

    [Test]
    public async Task Validate_StripeCanaryWebhookTimeoutSeconds_EnforcesBounds()
    {
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryWebhookTimeoutSeconds, "4")).IsNotNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryWebhookTimeoutSeconds, "301")).IsNotNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryWebhookTimeoutSeconds, "40")).IsNull();
    }

    [Test]
    public async Task Validate_StripeCanaryConsecutiveFailuresToAlert_EnforcesBounds()
    {
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryConsecutiveFailuresToAlert, "0")).IsNotNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryConsecutiveFailuresToAlert, "101")).IsNotNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryConsecutiveFailuresToAlert, "3")).IsNull();
    }
}
