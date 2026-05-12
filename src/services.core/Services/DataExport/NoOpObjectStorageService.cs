// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.DataExport;

/// <summary>
/// No-op implementation of <see cref="IObjectStorageService"/> when object storage is not configured.
/// All operations return a failure indicating the feature is unavailable.
/// </summary>
public sealed class NoOpObjectStorageService : IObjectStorageService
{
    /// <inheritdoc/>
    public Task<string> UploadFileAsync(string key, string filePath, CancellationToken ct)
    {
        throw new NotSupportedException("Object storage is not configured. Data export requires S3-compatible storage.");
    }

    /// <inheritdoc/>
    public Task<string> GeneratePresignedUrlAsync(string key, TimeSpan expiry)
    {
        throw new NotSupportedException("Object storage is not configured. Data export requires S3-compatible storage.");
    }

    /// <inheritdoc/>
    public Task<Stream> GetObjectStreamAsync(string key, CancellationToken ct)
    {
        throw new NotSupportedException("Object storage is not configured. Data export requires S3-compatible storage.");
    }

    /// <inheritdoc/>
    public Task DeleteObjectAsync(string key, CancellationToken ct)
    {
        throw new NotSupportedException("Object storage is not configured. Data export requires S3-compatible storage.");
    }
}
