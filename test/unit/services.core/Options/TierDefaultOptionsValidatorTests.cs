// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Validators;

/// <summary>
/// Tests for <see cref="TierDefaultOptionsValidator"/>. The export cooldown is an integer whose
/// zero default reads as "no limit", so a section that failed to bind would disable an entitlement
/// instead of breaking. In the hosted deployment that must stop the process.
/// </summary>
public sealed class TierDefaultOptionsValidatorTests
{
    private static TierDefaultOptionsValidator CreateValidator(bool selfHosted)
    {
        DeploymentMode mode = new(Options.Create(new DeploymentOptions { SelfHosted = selfHosted }));

        return new TierDefaultOptionsValidator(mode);
    }

    private static TierDefaultOptions ValidOptions()
    {
        return new TierDefaultOptions
        {
            Free = new TierLimitDefaults { DataExportCooldownHours = 24 },
            Pro = new TierLimitDefaults { DataExportCooldownHours = 12 },
            Team = new TierLimitDefaults { DataExportCooldownHours = 8 },
        };
    }

    /// <summary>
    /// The shipped configuration passes.
    /// </summary>
    [Test]
    public async Task Validate_HostedWithAllWindowsSet_Succeeds()
    {
        ValidateOptionsResult result = CreateValidator(selfHosted: false).Validate(null, ValidOptions());

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// An unbound section leaves every window at zero, which would silently remove the limit.
    /// </summary>
    [Test]
    public async Task Validate_HostedWithUnboundSection_Fails()
    {
        ValidateOptionsResult result = CreateValidator(selfHosted: false).Validate(null, new TierDefaultOptions());

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Free");
        await Assert.That(result.FailureMessage).Contains("Pro");
        await Assert.That(result.FailureMessage).Contains("Team");
    }

    /// <summary>
    /// One missing tier is enough to fail: a zero there disables the limit for that tier alone,
    /// which is harder to notice than all three being absent.
    /// </summary>
    [Test]
    public async Task Validate_HostedWithOneTierMissing_Fails()
    {
        TierDefaultOptions options = ValidOptions();
        options.Pro.DataExportCooldownHours = 0;

        ValidateOptionsResult result = CreateValidator(selfHosted: false).Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Pro");
    }

    /// <summary>
    /// A negative window is as meaningless as a zero one.
    /// </summary>
    [Test]
    public async Task Validate_HostedWithNegativeWindow_Fails()
    {
        TierDefaultOptions options = ValidOptions();
        options.Team.DataExportCooldownHours = -1;

        ValidateOptionsResult result = CreateValidator(selfHosted: false).Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
    }

    /// <summary>
    /// A self-hosted deployment has no tiers and no cost asymmetry to ration, so it must start
    /// with nothing configured.
    /// </summary>
    [Test]
    public async Task Validate_SelfHostedWithNothingConfigured_Succeeds()
    {
        ValidateOptionsResult result = CreateValidator(selfHosted: true).Validate(null, new TierDefaultOptions());

        await Assert.That(result.Succeeded).IsTrue();
    }
}
