// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Validators;

/// <summary>
/// Tests for <see cref="BillingOptionsValidator"/>.
/// </summary>
public sealed class BillingOptionsValidatorTests
{
    /// <summary>
    /// A certificate without its key would silently fall back to an unauthenticated channel that
    /// the billing API then rejects, so the pair must be configured together or not at all.
    /// </summary>
    [Test]
    public async Task Validate_CertificateWithoutKey_Fails()
    {
        BillingOptions options = new()
        {
            GrpcUrl = "https://billing-api.internal:12237",
            ClientCertificatePath = "/tls/internal-client/tls.crt",
        };

        ValidateOptionsResult result = new BillingOptionsValidator().Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("ClientCertificateKeyPath");
    }

    /// <summary>
    /// A key without its certificate is the mirror-image misconfiguration.
    /// </summary>
    [Test]
    public async Task Validate_KeyWithoutCertificate_Fails()
    {
        BillingOptions options = new()
        {
            GrpcUrl = "https://billing-api.internal:12237",
            ClientCertificateKeyPath = "/tls/internal-client/tls.key",
        };

        ValidateOptionsResult result = new BillingOptionsValidator().Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("ClientCertificatePath");
    }

    /// <summary>
    /// Both halves present is the production configuration.
    /// </summary>
    [Test]
    public async Task Validate_CertificateAndKeyTogether_Succeeds()
    {
        BillingOptions options = new()
        {
            GrpcUrl = "https://billing-api.internal:12237",
            ClientCertificatePath = "/tls/internal-client/tls.crt",
            ClientCertificateKeyPath = "/tls/internal-client/tls.key",
        };

        ValidateOptionsResult result = new BillingOptionsValidator().Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// An empty section is valid on its own; whether a billing endpoint is required is the
    /// deployment validator's decision, not this one's.
    /// </summary>
    [Test]
    public async Task Validate_EmptyOptions_Succeeds()
    {
        ValidateOptionsResult result = new BillingOptionsValidator().Validate(null, new BillingOptions());

        await Assert.That(result.Succeeded).IsTrue();
    }
}
