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
/// Tests for <see cref="IgnoredHostedConfigurationWarning"/>. The mistake this exists to catch is a
/// hosted deployment that loses its Deployment:SelfHosted setting: it boots self-hosted with a full
/// hosted configuration present and would otherwise report nothing while quietly serving the product
/// for free and discarding every email.
/// </summary>
public sealed class IgnoredHostedConfigurationWarningTests
{
    private static IgnoredHostedConfigurationWarning CreateService(
        bool selfHosted,
        string grpcUrl,
        out ILogger<IgnoredHostedConfigurationWarning> logger,
        string resendApiKey = "",
        string smtpHost = "")
    {
        logger = Substitute.For<ILogger<IgnoredHostedConfigurationWarning>>();

        DeploymentMode mode = new(Options.Create(new DeploymentOptions { SelfHosted = selfHosted }));
        IOptions<BillingOptions> billing = Options.Create(new BillingOptions { GrpcUrl = grpcUrl });
        IOptions<EmailOptions> email = Options.Create(new EmailOptions
        {
            Resend = new ResendEmailOptions { ApiKey = resendApiKey },
            Smtp = new SmtpEmailOptions { Host = smtpHost },
        });

        return new IgnoredHostedConfigurationWarning(mode, billing, email, logger);
    }

    private static void AssertWarningCount(ILogger<IgnoredHostedConfigurationWarning> logger, int expected)
    {
        logger.Received(expected).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task StartAsync_SelfHostedWithBillingConfigured_LogsWarningNamingTheFlag()
    {
        IgnoredHostedConfigurationWarning service = CreateService(
            selfHosted: true,
            grpcUrl: "https://billing.internal:443",
            out ILogger<IgnoredHostedConfigurationWarning> logger);

        await service.StartAsync(CancellationToken.None);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("Deployment:SelfHosted")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// The quieter half of the same mistake: with a Resend key and no SMTP host, a self-hosted
    /// deployment resolves the no-op transport and every send reports Skipped, which callers treat
    /// as terminal success and never retry.
    /// </summary>
    [Test]
    public async Task StartAsync_SelfHostedWithResendKeyAndNoSmtpHost_WarnsAboutSilentEmailLoss()
    {
        IgnoredHostedConfigurationWarning service = CreateService(
            selfHosted: true,
            grpcUrl: string.Empty,
            out ILogger<IgnoredHostedConfigurationWarning> logger,
            resendApiKey: "re_live_key");

        await service.StartAsync(CancellationToken.None);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("skipped silently")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// A self-hoster who has deliberately configured SMTP has a working transport, so the leftover
    /// Resend key is inert rather than a silent failure and must not produce a warning.
    /// </summary>
    [Test]
    public async Task StartAsync_SelfHostedWithResendKeyButSmtpConfigured_LogsNothingAboutEmail()
    {
        IgnoredHostedConfigurationWarning service = CreateService(
            selfHosted: true,
            grpcUrl: string.Empty,
            out ILogger<IgnoredHostedConfigurationWarning> logger,
            resendApiKey: "re_live_key",
            smtpHost: "smtp.example.com");

        await service.StartAsync(CancellationToken.None);

        AssertWarningCount(logger, 0);
    }

    [Test]
    public async Task StartAsync_SelfHostedWithBillingAndEmailConfigured_WarnsAboutBoth()
    {
        IgnoredHostedConfigurationWarning service = CreateService(
            selfHosted: true,
            grpcUrl: "https://billing.internal:443",
            out ILogger<IgnoredHostedConfigurationWarning> logger,
            resendApiKey: "re_live_key");

        await service.StartAsync(CancellationToken.None);

        AssertWarningCount(logger, 2);
    }

    [Test]
    public async Task StartAsync_SelfHostedWithNothingConfigured_LogsNothing()
    {
        IgnoredHostedConfigurationWarning service = CreateService(
            selfHosted: true,
            grpcUrl: string.Empty,
            out ILogger<IgnoredHostedConfigurationWarning> logger);

        await service.StartAsync(CancellationToken.None);

        AssertWarningCount(logger, 0);
    }

    /// <summary>
    /// The normal production case. A warning here would fire on every hosted boot and train
    /// operators to ignore the one message that matters.
    /// </summary>
    [Test]
    public async Task StartAsync_SaasWithEverythingConfigured_LogsNothing()
    {
        IgnoredHostedConfigurationWarning service = CreateService(
            selfHosted: false,
            grpcUrl: "https://billing.internal:443",
            out ILogger<IgnoredHostedConfigurationWarning> logger,
            resendApiKey: "re_live_key");

        await service.StartAsync(CancellationToken.None);

        AssertWarningCount(logger, 0);
    }

    [Test]
    public async Task Constructor_NullDeploymentMode_Throws()
    {
        await Assert.That(() => new IgnoredHostedConfigurationWarning(
                null!,
                Options.Create(new BillingOptions()),
                Options.Create(new EmailOptions()),
                Substitute.For<ILogger<IgnoredHostedConfigurationWarning>>()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullBillingOptions_Throws()
    {
        await Assert.That(() => new IgnoredHostedConfigurationWarning(
                new DeploymentMode(Options.Create(new DeploymentOptions())),
                null!,
                Options.Create(new EmailOptions()),
                Substitute.For<ILogger<IgnoredHostedConfigurationWarning>>()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullEmailOptions_Throws()
    {
        await Assert.That(() => new IgnoredHostedConfigurationWarning(
                new DeploymentMode(Options.Create(new DeploymentOptions())),
                Options.Create(new BillingOptions()),
                null!,
                Substitute.For<ILogger<IgnoredHostedConfigurationWarning>>()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        await Assert.That(() => new IgnoredHostedConfigurationWarning(
                new DeploymentMode(Options.Create(new DeploymentOptions())),
                Options.Create(new BillingOptions()),
                Options.Create(new EmailOptions()),
                null!))
            .Throws<ArgumentNullException>();
    }
}
