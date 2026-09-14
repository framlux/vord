// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Validators;

/// <summary>
/// Pins the rule that observability export is decided by the presence of an endpoint and by
/// nothing else. A deployment with no endpoint is a supported, silent configuration — a fresh
/// clone must start clean — so an absent endpoint is valid, not a validation failure.
/// </summary>
public sealed class ObservabilityOptionsValidatorTests
{
    [Test]
    public async Task Validate_NoEndpoint_SucceedsAndExportIsDisabled()
    {
        ObservabilityOptions options = new() { ServiceName = "api-server", OtlpEndpoint = null };
        ObservabilityOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(options.IsExportEnabled).IsFalse();
    }

    [Test]
    public async Task Validate_WhitespaceEndpoint_IsTreatedAsAbsent()
    {
        ObservabilityOptions options = new() { ServiceName = "api-server", OtlpEndpoint = "   " };
        ObservabilityOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(options.IsExportEnabled).IsFalse();
    }

    [Test]
    public async Task Validate_EndpointSetButServiceNameMissing_Fails()
    {
        ObservabilityOptions options = new() { ServiceName = "", OtlpEndpoint = "http://collector:4317" };
        ObservabilityOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
    }

    [Test]
    [Arguments("collector:4317")]
    [Arguments("otel-agent.framlux-observability.svc.cluster.local:4317")]
    [Arguments("grpc://collector:4317")]
    [Arguments("not a uri at all")]
    public async Task Validate_EndpointWithoutAnHttpScheme_Fails(string endpoint)
    {
        // A host:port with no scheme parses as an absolute URI whose scheme is the hostname, so
        // absoluteness alone lets the likeliest typo through to fail inside the exporter instead.
        ObservabilityOptions options = new() { ServiceName = "api-server", OtlpEndpoint = endpoint };
        ObservabilityOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
    }

    [Test]
    public async Task Validate_HttpsEndpoint_Succeeds()
    {
        ObservabilityOptions options = new() { ServiceName = "api-server", OtlpEndpoint = "https://collector:4318" };
        ObservabilityOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Validate_ValidEndpoint_SucceedsAndExportIsEnabled()
    {
        ObservabilityOptions options = new() { ServiceName = "api-server", OtlpEndpoint = "http://collector:4317" };
        ObservabilityOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(options.IsExportEnabled).IsTrue();
    }

    [Test]
    public async Task Validate_NullOptions_Throws()
    {
        ObservabilityOptionsValidator validator = new();

        await Assert.That(() => validator.Validate(null, null!)).Throws<ArgumentNullException>();
    }
}
