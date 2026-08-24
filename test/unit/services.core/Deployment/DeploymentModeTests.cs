// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Deployment;

/// <summary>
/// Tests for <see cref="DeploymentMode"/>. Every SaaS-only subsystem keys off this one value, so
/// the two properties must be exact negations with no third state.
/// </summary>
public sealed class DeploymentModeTests
{
    /// <summary>
    /// A self-hosted configuration reports self-hosted and never SaaS.
    /// </summary>
    [Test]
    public async Task IsSelfHosted_WhenConfiguredSelfHosted_IsTrueAndIsSaasIsFalse()
    {
        DeploymentMode mode = new(Options.Create(new DeploymentOptions { SelfHosted = true }));

        await Assert.That(mode.IsSelfHosted).IsTrue();
        await Assert.That(mode.IsSaas).IsFalse();
    }

    /// <summary>
    /// A SaaS configuration reports SaaS and never self-hosted.
    /// </summary>
    [Test]
    public async Task IsSaas_WhenConfiguredSaas_IsTrueAndIsSelfHostedIsFalse()
    {
        DeploymentMode mode = new(Options.Create(new DeploymentOptions { SelfHosted = false }));

        await Assert.That(mode.IsSaas).IsTrue();
        await Assert.That(mode.IsSelfHosted).IsFalse();
    }

    /// <summary>
    /// With nothing configured the process is self-hosted, matching a fresh clone of the repository.
    /// </summary>
    [Test]
    public async Task Constructor_DefaultOptions_IsSelfHosted()
    {
        DeploymentMode mode = new(Options.Create(new DeploymentOptions()));

        await Assert.That(mode.IsSelfHosted).IsTrue();
    }
}
