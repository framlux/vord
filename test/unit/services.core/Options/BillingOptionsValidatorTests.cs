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
    private readonly BillingOptionsValidator _validator = new();

    [Test]
    public async Task Validate_BillingDisabled_WithEmptyGrpcUrl_Succeeds()
    {
        BillingOptions options = new() { Enabled = false, GrpcUrl = string.Empty };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Validate_BillingDisabled_WithGrpcUrl_Succeeds()
    {
        BillingOptions options = new() { Enabled = false, GrpcUrl = "http://localhost:5001" };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Validate_BillingEnabled_WithGrpcUrl_Succeeds()
    {
        BillingOptions options = new() { Enabled = true, GrpcUrl = "http://localhost:5001" };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Validate_BillingEnabled_WithEmptyGrpcUrl_Fails()
    {
        BillingOptions options = new() { Enabled = true, GrpcUrl = string.Empty };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
    }

    [Test]
    public async Task Validate_BillingEnabled_WithWhitespaceGrpcUrl_Fails()
    {
        BillingOptions options = new() { Enabled = true, GrpcUrl = "   " };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
    }
}
