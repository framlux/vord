// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.DataExport;

namespace Framlux.FleetManagement.Test.Services.DataExport;

/// <summary>
/// Tests for <see cref="NoOpObjectStorageService"/>.
/// Verifies all operations throw <see cref="NotSupportedException"/>
/// when object storage is not configured.
/// </summary>
public sealed class NoOpObjectStorageServiceTests
{
    private static NoOpObjectStorageService CreateSut()
    {
        return new NoOpObjectStorageService();
    }

    // ========== UploadFileAsync ==========

    [Test]
    public async Task UploadFileAsync_ThrowsNotSupportedException()
    {
        NoOpObjectStorageService sut = CreateSut();

        NotSupportedException? ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.UploadFileAsync("test-key", "/tmp/test.db", CancellationToken.None));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("Object storage is not configured");
    }

    [Test]
    public async Task UploadFileAsync_WithEmptyKey_StillThrowsNotSupportedException()
    {
        NoOpObjectStorageService sut = CreateSut();

        NotSupportedException? ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.UploadFileAsync("", "", CancellationToken.None));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("S3-compatible storage");
    }

    [Test]
    public async Task UploadFileAsync_WithCancelledToken_StillThrowsNotSupportedException()
    {
        NoOpObjectStorageService sut = CreateSut();
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        NotSupportedException? ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.UploadFileAsync("key", "/path", cts.Token));

        await Assert.That(ex).IsNotNull();
    }

    // ========== GeneratePresignedUrlAsync ==========

    [Test]
    public async Task GeneratePresignedUrlAsync_ThrowsNotSupportedException()
    {
        NoOpObjectStorageService sut = CreateSut();

        NotSupportedException? ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.GeneratePresignedUrlAsync("test-key", TimeSpan.FromMinutes(5)));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("Object storage is not configured");
    }

    [Test]
    public async Task GeneratePresignedUrlAsync_WithZeroExpiry_StillThrowsNotSupportedException()
    {
        NoOpObjectStorageService sut = CreateSut();

        NotSupportedException? ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.GeneratePresignedUrlAsync("key", TimeSpan.Zero));

        await Assert.That(ex).IsNotNull();
    }

    // ========== GetObjectStreamAsync ==========

    [Test]
    public async Task GetObjectStreamAsync_ThrowsNotSupportedException()
    {
        NoOpObjectStorageService sut = CreateSut();

        NotSupportedException? ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.GetObjectStreamAsync("test-key", CancellationToken.None));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("Object storage is not configured");
    }

    [Test]
    public async Task GetObjectStreamAsync_WithEmptyKey_StillThrowsNotSupportedException()
    {
        NoOpObjectStorageService sut = CreateSut();

        NotSupportedException? ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.GetObjectStreamAsync("", CancellationToken.None));

        await Assert.That(ex).IsNotNull();
    }

    // ========== DeleteObjectAsync ==========

    [Test]
    public async Task DeleteObjectAsync_ThrowsNotSupportedException()
    {
        NoOpObjectStorageService sut = CreateSut();

        NotSupportedException? ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.DeleteObjectAsync("test-key", CancellationToken.None));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("Object storage is not configured");
    }

    [Test]
    public async Task DeleteObjectAsync_WithCancelledToken_StillThrowsNotSupportedException()
    {
        NoOpObjectStorageService sut = CreateSut();
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        NotSupportedException? ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.DeleteObjectAsync("key", cts.Token));

        await Assert.That(ex).IsNotNull();
    }

}
