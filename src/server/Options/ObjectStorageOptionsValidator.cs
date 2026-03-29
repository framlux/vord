// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Validates <see cref="ObjectStorageOptions"/> configuration.
/// When a bucket name is configured, all connection fields are required.
/// </summary>
public sealed class ObjectStorageOptionsValidator : IValidateOptions<ObjectStorageOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, ObjectStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BucketName))
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            failures.Add("ObjectStorage:Endpoint is required when ObjectStorage:BucketName is configured.");
        }

        if (string.IsNullOrWhiteSpace(options.AccessKey))
        {
            failures.Add("ObjectStorage:AccessKey is required when ObjectStorage:BucketName is configured.");
        }

        if (string.IsNullOrWhiteSpace(options.SecretKey))
        {
            failures.Add("ObjectStorage:SecretKey is required when ObjectStorage:BucketName is configured.");
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
    }
}
