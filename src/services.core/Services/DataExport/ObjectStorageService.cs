// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Amazon.S3;
using Amazon.S3.Model;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.DataExport;

/// <summary>
/// S3-compatible object storage service using the AWS SDK.
/// Works with any S3-compatible provider (AWS, MinIO, etc.).
/// Implements <see cref="IDisposable"/> so DI scope teardown disposes the underlying
/// <see cref="AmazonS3Client"/> — without that, the SDK's HttpClient/connection pool would
/// leak across <c>WebApplicationFactory</c> recycles in functional tests.
/// </summary>
public sealed class ObjectStorageService : IObjectStorageService, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucketName;
    private readonly ILogger<ObjectStorageService> _logger;
    private bool _disposed;

    /// <summary>
    /// Creates a new instance of the <see cref="ObjectStorageService"/> class.
    /// </summary>
    public ObjectStorageService(IOptions<ObjectStorageOptions> objectStorageOptions, ILogger<ObjectStorageService> logger)
    {
        ArgumentNullException.ThrowIfNull(objectStorageOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        ObjectStorageOptions opts = objectStorageOptions.Value;
        _bucketName = opts.BucketName;

        AmazonS3Config s3Config = new()
        {
            ServiceURL = opts.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = opts.Region
        };

        _client = new AmazonS3Client(opts.AccessKey, opts.SecretKey, s3Config);
    }

    /// <inheritdoc/>
    public async Task<string> UploadFileAsync(string key, string filePath, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        PutObjectRequest request = new()
        {
            BucketName = _bucketName,
            Key = key,
            InputStream = fileStream,
            ContentType = "application/x-sqlite3"
        };

        await _client.PutObjectAsync(request, ct);
        _logger.LogInformation("Uploaded export file to {Bucket}/{Key}", _bucketName, key);

        return key;
    }

    /// <inheritdoc/>
    public async Task<string> GeneratePresignedUrlAsync(string key, TimeSpan expiry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GetPreSignedUrlRequest request = new()
        {
            BucketName = _bucketName,
            Key = key,
            Expires = DateTime.UtcNow.Add(expiry),
            Verb = HttpVerb.GET
        };

        string url = await _client.GetPreSignedURLAsync(request);

        return url;
    }

    /// <inheritdoc/>
    public async Task<Stream> GetObjectStreamAsync(string key, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GetObjectRequest request = new()
        {
            BucketName = _bucketName,
            Key = key
        };

        GetObjectResponse response = await _client.GetObjectAsync(request, ct);

        return response.ResponseStream;
    }

    /// <inheritdoc/>
    public async Task DeleteObjectAsync(string key, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DeleteObjectRequest request = new()
        {
            BucketName = _bucketName,
            Key = key
        };

        await _client.DeleteObjectAsync(request, ct);
        _logger.LogInformation("Deleted export file {Bucket}/{Key}", _bucketName, key);
    }

    /// <summary>
    /// Disposes the underlying <see cref="AmazonS3Client"/>. Idempotent: subsequent calls are
    /// no-ops. Public methods on a disposed instance throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _client.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
