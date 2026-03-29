// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Configuration options for S3-compatible object storage (e.g., SeaweedFS).
/// </summary>
public sealed class ObjectStorageOptions
{
    /// <summary>
    /// The storage bucket name. When empty, object storage is disabled.
    /// </summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// The S3-compatible endpoint URL.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// The access key for authentication.
    /// </summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>
    /// The secret key for authentication.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// The storage region.
    /// </summary>
    public string Region { get; set; } = "us-east-1";
}
