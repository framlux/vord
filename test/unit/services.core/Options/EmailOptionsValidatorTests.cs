// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Validators;

/// <summary>
/// Tests for <see cref="EmailOptionsValidator"/>. Email is optional for a self-hosted deployment
/// and mandatory for the hosted one — treating a missing key as "email is off" in SaaS is exactly
/// the overloaded signal this design removes, so there it must stop the process.
/// </summary>
public sealed class EmailOptionsValidatorTests
{
    private static EmailOptionsValidator CreateValidator(bool selfHosted)
    {
        DeploymentMode mode = new(Options.Create(new DeploymentOptions { SelfHosted = selfHosted }));

        return new EmailOptionsValidator(mode);
    }

    /// <summary>
    /// A self-hosted deployment with no email configured at all is supported: sends are skipped.
    /// </summary>
    [Test]
    public async Task Validate_SelfHostedWithNothingConfigured_Succeeds()
    {
        ValidateOptionsResult result = CreateValidator(selfHosted: true).Validate(null, new EmailOptions());

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// A self-hosted deployment with SMTP configured must also declare a sender address.
    /// </summary>
    [Test]
    public async Task Validate_SelfHostedSmtpHostWithoutFromEmail_Fails()
    {
        EmailOptions options = new() { Smtp = new SmtpEmailOptions { Host = "smtp.example.com" } };

        ValidateOptionsResult result = CreateValidator(selfHosted: true).Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Email:FromEmail");
    }

    /// <summary>
    /// A fully configured SMTP relay is the normal self-hosted deployment.
    /// </summary>
    [Test]
    public async Task Validate_SelfHostedSmtpFullyConfigured_Succeeds()
    {
        EmailOptions options = new()
        {
            FromEmail = "alerts@example.com",
            Smtp = new SmtpEmailOptions { Host = "smtp.example.com", Port = 587 },
        };

        ValidateOptionsResult result = CreateValidator(selfHosted: true).Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// Port zero would silently mean "provider default" in some clients and fail in others, so it
    /// is rejected rather than guessed at.
    /// </summary>
    [Test]
    public async Task Validate_SelfHostedSmtpPortOutOfRange_Fails()
    {
        EmailOptions options = new()
        {
            FromEmail = "alerts@example.com",
            Smtp = new SmtpEmailOptions { Host = "smtp.example.com", Port = 0 },
        };

        ValidateOptionsResult result = CreateValidator(selfHosted: true).Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Email:Smtp:Port");
    }

    /// <summary>
    /// In the hosted deployment a missing Resend key is always a misconfiguration, never a
    /// deployment style, so it stops startup instead of silently disabling invitations.
    /// </summary>
    [Test]
    public async Task Validate_SaasWithoutResendApiKey_Fails()
    {
        EmailOptions options = new() { FromEmail = "alerts@example.com" };

        ValidateOptionsResult result = CreateValidator(selfHosted: false).Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Email:Resend:ApiKey");
    }

    /// <summary>
    /// Resend rejects any send from an unverified address and the rejection is only visible in
    /// logs, so a missing sender is a startup failure too.
    /// </summary>
    [Test]
    public async Task Validate_SaasWithApiKeyButNoFromEmail_Fails()
    {
        EmailOptions options = new() { Resend = new ResendEmailOptions { ApiKey = "re_test" } };

        ValidateOptionsResult result = CreateValidator(selfHosted: false).Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Email:FromEmail");
    }

    /// <summary>
    /// The hosted deployment with a key and a verified sender is the normal production case.
    /// </summary>
    [Test]
    public async Task Validate_SaasFullyConfigured_Succeeds()
    {
        EmailOptions options = new()
        {
            FromEmail = "Framlux Vord <invitations@outreach.framlux.io>",
            Resend = new ResendEmailOptions { ApiKey = "re_test" },
        };

        ValidateOptionsResult result = CreateValidator(selfHosted: false).Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// A null options instance is a programming error, not a configuration one.
    /// </summary>
    [Test]
    public async Task Validate_NullOptions_Throws()
    {
        EmailOptionsValidator validator = CreateValidator(selfHosted: true);

        await Assert.That(() => validator.Validate(null, null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullDeploymentMode_Throws()
    {
        await Assert.That(() => new EmailOptionsValidator(null!)).Throws<ArgumentNullException>();
    }
}
