// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Validators;

/// <summary>
/// Tests for <see cref="ObjectStorageOptionsValidator"/>.
/// </summary>
public sealed class ObjectStorageOptionsValidatorTests
{
    private readonly ObjectStorageOptionsValidator _validator = new();

    [Test]
    public async Task Validate_EmptyBucketName_Succeeds()
    {
        ObjectStorageOptions options = new() { BucketName = string.Empty };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Validate_AllFieldsProvided_Succeeds()
    {
        ObjectStorageOptions options = new()
        {
            BucketName = "my-bucket",
            Endpoint = "http://localhost:9000",
            AccessKey = "access",
            SecretKey = "secret",
            Region = "us-east-1"
        };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Validate_BucketNameSet_MissingEndpoint_Fails()
    {
        ObjectStorageOptions options = new()
        {
            BucketName = "my-bucket",
            Endpoint = string.Empty,
            AccessKey = "access",
            SecretKey = "secret"
        };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
    }

    [Test]
    public async Task Validate_BucketNameSet_MissingAccessKey_Fails()
    {
        ObjectStorageOptions options = new()
        {
            BucketName = "my-bucket",
            Endpoint = "http://localhost:9000",
            AccessKey = string.Empty,
            SecretKey = "secret"
        };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
    }

    [Test]
    public async Task Validate_BucketNameSet_MissingSecretKey_Fails()
    {
        ObjectStorageOptions options = new()
        {
            BucketName = "my-bucket",
            Endpoint = "http://localhost:9000",
            AccessKey = "access",
            SecretKey = string.Empty
        };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
    }

    [Test]
    public async Task Validate_BucketNameSet_AllRequiredMissing_FailsWithMultipleErrors()
    {
        ObjectStorageOptions options = new()
        {
            BucketName = "my-bucket",
            Endpoint = string.Empty,
            AccessKey = string.Empty,
            SecretKey = string.Empty
        };

        ValidateOptionsResult result = _validator.Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.Failures.Count()).IsEqualTo(3);
    }
}
