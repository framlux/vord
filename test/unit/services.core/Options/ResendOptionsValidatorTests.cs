// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Validators;

/// <summary>
/// Tests for <see cref="ResendOptionsValidator"/>. The rule is conditional: email is optional, so
/// no API key is a valid deployment, but an API key without a sender address is not — Resend
/// rejects those sends and the only trace is a log line, so it must fail at startup instead.
/// </summary>
public sealed class ResendOptionsValidatorTests
{
    /// <summary>
    /// A deployment with email switched off entirely is valid — the service skips sending.
    /// </summary>
    [Test]
    public async Task Validate_NoApiKey_Succeeds()
    {
        ResendOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(null, new ResendOptions());

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// A sender address without an API key is harmless: nothing will be sent either way.
    /// </summary>
    [Test]
    public async Task Validate_FromEmailWithoutApiKey_Succeeds()
    {
        ResendOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            null,
            new ResendOptions { FromEmail = "Framlux Vord <invitations@outreach.framlux.io>" });

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// Both configured is the normal production case.
    /// </summary>
    [Test]
    public async Task Validate_ApiKeyAndFromEmail_Succeeds()
    {
        ResendOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            null,
            new ResendOptions
            {
                ApiKey = "re_test_key",
                FromEmail = "Framlux Vord <invitations@outreach.framlux.io>",
            });

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// The regression this validator exists for: an API key with no sender address shipped to
    /// production and every invitation was rejected by Resend, visible only in logs.
    /// </summary>
    [Test]
    public async Task Validate_ApiKeyWithoutFromEmail_Fails()
    {
        ResendOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            null,
            new ResendOptions { ApiKey = "re_test_key" });

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Resend:FromEmail is required");
    }

    /// <summary>
    /// Whitespace is not a sender address.
    /// </summary>
    [Test]
    public async Task Validate_ApiKeyWithWhitespaceFromEmail_Fails()
    {
        ResendOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            null,
            new ResendOptions { ApiKey = "re_test_key", FromEmail = "   " });

        await Assert.That(result.Failed).IsTrue();
    }

    /// <summary>
    /// A whitespace-only API key counts as unconfigured, so no sender is required.
    /// </summary>
    [Test]
    public async Task Validate_WhitespaceApiKey_Succeeds()
    {
        ResendOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            null,
            new ResendOptions { ApiKey = "   " });

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// A null options instance is a programming error and must not be silently accepted.
    /// </summary>
    [Test]
    public async Task Validate_NullOptions_ThrowsArgumentNullException()
    {
        ResendOptionsValidator validator = new();

        await Assert.That(() => validator.Validate(null, null!)).Throws<ArgumentNullException>();
    }
}
