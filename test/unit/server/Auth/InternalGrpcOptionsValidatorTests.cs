// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// A half-configured mutual-TLS endpoint must stop the process at startup rather than come up
/// in a state where the operator believes callers are being authorised and they are not.
/// </summary>
public sealed class InternalGrpcOptionsValidatorTests
{
    private static InternalGrpcOptions CreateValidOptions()
    {
        return new InternalGrpcOptions
        {
            Enabled = true,
            Port = 12236,
            CertificatePath = "/tls/internal/tls.crt",
            CertificateKeyPath = "/tls/internal/tls.key",
            ClientCaPath = "/tls/internal/ca.crt",
            AllowedClientSubjects = { "billing-api.vord-fleet.svc.cluster.local" }
        };
    }

    [Test]
    public async Task Validate_Disabled_IgnoresEverythingElse()
    {
        InternalGrpcOptionsValidator validator = new();
        InternalGrpcOptions options = new() { Enabled = false };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Validate_FullyConfigured_Succeeds()
    {
        InternalGrpcOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(name: null, CreateValidOptions());

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// The most important failure: an enabled endpoint with no permitted subjects would accept
    /// any certificate the internal CA issued, so it must not be allowed to start.
    /// </summary>
    [Test]
    public async Task Validate_EnabledWithNoAllowedSubjects_Fails()
    {
        InternalGrpcOptionsValidator validator = new();
        InternalGrpcOptions options = CreateValidOptions();
        options.AllowedClientSubjects.Clear();

        ValidateOptionsResult result = validator.Validate(name: null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("AllowedClientSubjects");
    }

    [Test]
    public async Task Validate_EnabledWithoutCertificatePath_Fails()
    {
        InternalGrpcOptionsValidator validator = new();
        InternalGrpcOptions options = CreateValidOptions();
        options.CertificatePath = string.Empty;

        ValidateOptionsResult result = validator.Validate(name: null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("CertificatePath");
    }

    [Test]
    public async Task Validate_EnabledWithoutCertificateKeyPath_Fails()
    {
        InternalGrpcOptionsValidator validator = new();
        InternalGrpcOptions options = CreateValidOptions();
        options.CertificateKeyPath = string.Empty;

        ValidateOptionsResult result = validator.Validate(name: null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("CertificateKeyPath");
    }

    [Test]
    public async Task Validate_EnabledWithoutClientCaPath_Fails()
    {
        InternalGrpcOptionsValidator validator = new();
        InternalGrpcOptions options = CreateValidOptions();
        options.ClientCaPath = string.Empty;

        ValidateOptionsResult result = validator.Validate(name: null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("ClientCaPath");
    }

    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(65536)]
    [Test]
    public async Task Validate_PortOutOfRange_Fails(int port)
    {
        InternalGrpcOptionsValidator validator = new();
        InternalGrpcOptions options = CreateValidOptions();
        options.Port = port;

        ValidateOptionsResult result = validator.Validate(name: null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Port");
    }
}
