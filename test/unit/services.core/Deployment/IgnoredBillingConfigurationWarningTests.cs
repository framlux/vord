// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Deployment;

/// <summary>
/// Tests for <see cref="IgnoredBillingConfigurationWarning"/>. The mistake this exists to catch is a
/// hosted deployment that loses its Deployment:SelfHosted setting: it boots self-hosted with a full
/// billing configuration present and would otherwise report nothing while quietly serving the
/// product for free.
/// </summary>
public sealed class IgnoredBillingConfigurationWarningTests
{
    private static IgnoredBillingConfigurationWarning CreateService(
        bool selfHosted,
        string grpcUrl,
        out ILogger<IgnoredBillingConfigurationWarning> logger)
    {
        logger = Substitute.For<ILogger<IgnoredBillingConfigurationWarning>>();

        DeploymentMode mode = new(Options.Create(new DeploymentOptions { SelfHosted = selfHosted }));
        IOptions<BillingOptions> billing = Options.Create(new BillingOptions { GrpcUrl = grpcUrl });

        return new IgnoredBillingConfigurationWarning(mode, billing, logger);
    }

    [Test]
    public async Task StartAsync_SelfHostedWithBillingConfigured_LogsWarning()
    {
        IgnoredBillingConfigurationWarning service = CreateService(
            selfHosted: true,
            grpcUrl: "https://billing.internal:443",
            out ILogger<IgnoredBillingConfigurationWarning> logger);

        await service.StartAsync(CancellationToken.None);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("Deployment:SelfHosted")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task StartAsync_SelfHostedWithoutBillingConfigured_LogsNothing()
    {
        IgnoredBillingConfigurationWarning service = CreateService(
            selfHosted: true,
            grpcUrl: string.Empty,
            out ILogger<IgnoredBillingConfigurationWarning> logger);

        await service.StartAsync(CancellationToken.None);

        logger.DidNotReceive().Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// The normal production case. A warning here would fire on every hosted boot and train
    /// operators to ignore the one message that matters.
    /// </summary>
    [Test]
    public async Task StartAsync_SaasWithBillingConfigured_LogsNothing()
    {
        IgnoredBillingConfigurationWarning service = CreateService(
            selfHosted: false,
            grpcUrl: "https://billing.internal:443",
            out ILogger<IgnoredBillingConfigurationWarning> logger);

        await service.StartAsync(CancellationToken.None);

        logger.DidNotReceive().Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task Constructor_NullDeploymentMode_Throws()
    {
        IOptions<BillingOptions> billing = Options.Create(new BillingOptions());
        ILogger<IgnoredBillingConfigurationWarning> logger = Substitute.For<ILogger<IgnoredBillingConfigurationWarning>>();

        await Assert.That(() => new IgnoredBillingConfigurationWarning(null!, billing, logger))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullBillingOptions_Throws()
    {
        DeploymentMode mode = new(Options.Create(new DeploymentOptions()));
        ILogger<IgnoredBillingConfigurationWarning> logger = Substitute.For<ILogger<IgnoredBillingConfigurationWarning>>();

        await Assert.That(() => new IgnoredBillingConfigurationWarning(mode, null!, logger))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        DeploymentMode mode = new(Options.Create(new DeploymentOptions()));
        IOptions<BillingOptions> billing = Options.Create(new BillingOptions());

        await Assert.That(() => new IgnoredBillingConfigurationWarning(mode, billing, null!))
            .Throws<ArgumentNullException>();
    }
}
