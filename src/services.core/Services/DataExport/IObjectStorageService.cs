// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.DataExport;

/// <summary>
/// Provides object storage operations for S3-compatible services.
/// </summary>
public interface IObjectStorageService
{
    /// <summary>
    /// Uploads a file to object storage.
    /// </summary>
    /// <param name="key">The object key (path) in the bucket.</param>
    /// <param name="filePath">The local file path to upload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The object key of the uploaded file.</returns>
    Task<string> UploadFileAsync(string key, string filePath, CancellationToken ct);

    /// <summary>
    /// Generates a pre-signed URL for downloading an object.
    /// </summary>
    /// <param name="key">The object key in the bucket.</param>
    /// <param name="expiry">How long the URL should be valid.</param>
    /// <returns>A pre-signed download URL.</returns>
    Task<string> GeneratePresignedUrlAsync(string key, TimeSpan expiry);

    /// <summary>
    /// Gets a readable stream for an object in storage.
    /// The caller is responsible for disposing the returned stream.
    /// </summary>
    /// <param name="key">The object key in the bucket.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A stream containing the object data.</returns>
    Task<Stream> GetObjectStreamAsync(string key, CancellationToken ct);

    /// <summary>
    /// Deletes an object from storage.
    /// </summary>
    /// <param name="key">The object key to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteObjectAsync(string key, CancellationToken ct);
}
