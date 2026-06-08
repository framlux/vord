// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.DataExport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="ObjectStorageService"/> constructor validation.
/// </summary>
public sealed class ObjectStorageServiceTests
{
    private static IOptions<ObjectStorageOptions> BuildOptions(
        string bucketName = "test-bucket",
        string endpoint = "https://s3.example.com",
        string accessKey = "AKIA123",
        string secretKey = "secret123",
        string region = "us-east-1")
    {
        return Options.Create(new ObjectStorageOptions
        {
            BucketName = bucketName,
            Endpoint = endpoint,
            AccessKey = accessKey,
            SecretKey = secretKey,
            Region = region
        });
    }

    [Test]
    public async Task Constructor_NullOptions_ThrowsArgumentNullException()
    {
        ILogger<ObjectStorageService> logger = Substitute.For<ILogger<ObjectStorageService>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            ObjectStorageService _ = new(null!, logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("objectStorageOptions");
    }

    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        IOptions<ObjectStorageOptions> options = BuildOptions();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            ObjectStorageService _ = new(options, null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    [Test]
    public async Task Constructor_CustomRegion_UsesProvidedRegion()
    {
        ILogger<ObjectStorageService> logger = Substitute.For<ILogger<ObjectStorageService>>();
        IOptions<ObjectStorageOptions> options = BuildOptions(region: "ap-southeast-1");

        Exception? thrown = null;
        try
        {
            ObjectStorageService _ = new(options, logger);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNull();
    }

    // ==========================================================================================
    // M10 regression: IDisposable semantics — Dispose is idempotent, post-Dispose method calls
    // throw ObjectDisposedException.
    // ==========================================================================================

    [Test]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        ObjectStorageService svc = new(BuildOptions(), Substitute.For<ILogger<ObjectStorageService>>());

        Exception? caught = null;
        try
        {
            svc.Dispose();
            svc.Dispose();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNull();
    }

    [Test]
    public async Task UploadFileAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        ObjectStorageService svc = new(BuildOptions(), Substitute.For<ILogger<ObjectStorageService>>());
        svc.Dispose();

        ObjectDisposedException? ex = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            svc.UploadFileAsync("k", "/tmp/anything", CancellationToken.None));

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task DeleteObjectAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        ObjectStorageService svc = new(BuildOptions(), Substitute.For<ILogger<ObjectStorageService>>());
        svc.Dispose();

        ObjectDisposedException? ex = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            svc.DeleteObjectAsync("k", CancellationToken.None));

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task GeneratePresignedUrlAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        ObjectStorageService svc = new(BuildOptions(), Substitute.For<ILogger<ObjectStorageService>>());
        svc.Dispose();

        ObjectDisposedException? ex = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            svc.GeneratePresignedUrlAsync("k", TimeSpan.FromMinutes(5)));

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task GetObjectStreamAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        ObjectStorageService svc = new(BuildOptions(), Substitute.For<ILogger<ObjectStorageService>>());
        svc.Dispose();

        ObjectDisposedException? ex = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            svc.GetObjectStreamAsync("k", CancellationToken.None));

        await Assert.That(ex).IsNotNull();
    }
}
