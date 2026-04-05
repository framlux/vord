// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.DataExport;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="DataExportHandler"/>.
/// </summary>
public class DataExportHandlerTests
{
    private static DataExportHandler CreateHandler(
        TestDatabaseFactory dbFactory,
        IObjectStorageService? objectStorage = null,
        ILogger<DataExportHandler>? logger = null)
    {
        objectStorage ??= new CaptureObjectStorageService();
        logger ??= Substitute.For<ILogger<DataExportHandler>>();

        return new DataExportHandler(dbFactory.Context, logger, objectStorage);
    }

    private static async Task<long> SeedMachine(TestDatabaseFactory dbFactory, int tenantId = 1, bool isDeleted = false, string hostname = "export-host")
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: hostname);
        machine.IsDeleted = isDeleted;
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        return machine.Id;
    }

    // ========== Constructor tests ==========

    [Test]
    public async Task Constructor_NullDatabaseContext_ThrowsArgumentNullException()
    {
        IObjectStorageService objectStorage = new CaptureObjectStorageService();
        ILogger<DataExportHandler> logger = Substitute.For<ILogger<DataExportHandler>>();

        await Assert.That(() =>
            new DataExportHandler(null!, logger, objectStorage))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IObjectStorageService objectStorage = new CaptureObjectStorageService();

        await Assert.That(() =>
            new DataExportHandler(dbFactory.Context, null!, objectStorage))
            .Throws<ArgumentNullException>();
    }

    // ========== ExportTenantDataAsync tests ==========

    [Test]
    public async Task ExportTenantDataAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<int> result = await handler.ExportTenantDataAsync(null, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task ExportTenantDataAsync_ValidTenant_CreatesJobWithPendingStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<int> result = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data).IsGreaterThan(0);

        DataExportJob? job = await dbFactory.Context.DataExportJobs
            .FirstOrDefaultAsync(j => j.Id == result.Data);
        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Status).IsEqualTo(DataExportJobStatus.Pending);
        await Assert.That(job.TenantId).IsEqualTo(1);
    }

    // ========== ProcessExportJobAsync tests ==========

    [Test]
    public async Task ProcessExportJobAsync_TenantWithMachines_ProducesValidSqliteFile()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1, hostname: "web-01");
        await SeedMachine(dbFactory, tenantId: 1, hostname: "web-02");

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        int jobId = createResult.Data;
        await handler.ProcessExportJobAsync(jobId, CancellationToken.None);

        try
        {
            await Assert.That(capture.LastCapturedPath).IsNotNull();

            using SqliteConnection sqlite = new($"Data Source={capture.LastCapturedPath}");
            await sqlite.OpenAsync();

            using SqliteCommand cmd = new("SELECT COUNT(*) FROM Machines", sqlite);
            long machineCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());

            await Assert.That(machineCount).IsEqualTo(2);
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    [Test]
    public async Task ProcessExportJobAsync_DeletedMachines_ExcludedFromExport()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1, hostname: "active-host");
        await SeedMachine(dbFactory, tenantId: 1, isDeleted: true, hostname: "deleted-host");

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data, CancellationToken.None);

        try
        {
            using SqliteConnection sqlite = new($"Data Source={capture.LastCapturedPath}");
            await sqlite.OpenAsync();

            using SqliteCommand cmd = new("SELECT COUNT(*) FROM Machines", sqlite);
            long machineCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());

            await Assert.That(machineCount).IsEqualTo(1);
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    [Test]
    public async Task ProcessExportJobAsync_CrossTenantIsolation_OnlyExportsOwnMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1, hostname: "tenant1-host");
        await SeedMachine(dbFactory, tenantId: 2, hostname: "tenant2-host");

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data, CancellationToken.None);

        try
        {
            using SqliteConnection sqlite = new($"Data Source={capture.LastCapturedPath}");
            await sqlite.OpenAsync();

            using SqliteCommand cmd = new("SELECT Name FROM Machines", sqlite);
            using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
            List<string> names = [];
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            await Assert.That(names.Count).IsEqualTo(1);
            await Assert.That(names[0]).IsEqualTo("tenant1-host");
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    [Test]
    public async Task ProcessExportJobAsync_MachineWithState_ExportsMachineState()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, tenantId: 1);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machineId, cpuPercent: 42);
        await dbFactory.Context.InsertAsync(state);

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data, CancellationToken.None);

        try
        {
            using SqliteConnection sqlite = new($"Data Source={capture.LastCapturedPath}");
            await sqlite.OpenAsync();

            using SqliteCommand cmd = new("SELECT CpuUsagePercent FROM MachineStateSummary WHERE MachineId = @mid", sqlite);
            cmd.Parameters.AddWithValue("@mid", machineId);
            object? cpuObj = await cmd.ExecuteScalarAsync();

            await Assert.That(Convert.ToInt32(cpuObj)).IsEqualTo(42);
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    [Test]
    public async Task ProcessExportJobAsync_MachineWithTelemetry_ExportsTelemetryRecords()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, tenantId: 1);

        for (int i = 0; i < 3; i++)
        {
            MachineTelemetry t = TestDataBuilder.BuildMachineTelemetry(machineId: machineId);
            await dbFactory.Context.InsertWithInt64IdentityAsync(t);
        }

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data, CancellationToken.None);

        try
        {
            using SqliteConnection sqlite = new($"Data Source={capture.LastCapturedPath}");
            await sqlite.OpenAsync();

            using SqliteCommand cmd = new("SELECT COUNT(*) FROM MachineTelemetry", sqlite);
            long telemetryCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());

            await Assert.That(telemetryCount).IsEqualTo(3);
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    [Test]
    public async Task ProcessExportJobAsync_SoftDeletedTelemetry_ExcludedFromExport()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, tenantId: 1);

        MachineTelemetry active = TestDataBuilder.BuildMachineTelemetry(machineId: machineId);
        await dbFactory.Context.InsertWithInt64IdentityAsync(active);

        MachineTelemetry deleted = TestDataBuilder.BuildMachineTelemetry(machineId: machineId);
        deleted.DeletedAt = DateTimeOffset.UtcNow;
        await dbFactory.Context.InsertWithInt64IdentityAsync(deleted);

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data, CancellationToken.None);

        try
        {
            using SqliteConnection sqlite = new($"Data Source={capture.LastCapturedPath}");
            await sqlite.OpenAsync();

            using SqliteCommand cmd = new("SELECT COUNT(*) FROM MachineTelemetry", sqlite);
            long telemetryCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());

            await Assert.That(telemetryCount).IsEqualTo(1);
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    [Test]
    public async Task ProcessExportJobAsync_MultipleTelemetryTypes_AllExported()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, tenantId: 1);

        MachineTelemetry t1 = TestDataBuilder.BuildMachineTelemetry(machineId: machineId, telemetryType: 1);
        MachineTelemetry t2 = TestDataBuilder.BuildMachineTelemetry(machineId: machineId, telemetryType: 2);
        MachineTelemetry t3 = TestDataBuilder.BuildMachineTelemetry(machineId: machineId, telemetryType: 3);
        await dbFactory.Context.InsertWithInt64IdentityAsync(t1);
        await dbFactory.Context.InsertWithInt64IdentityAsync(t2);
        await dbFactory.Context.InsertWithInt64IdentityAsync(t3);

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data, CancellationToken.None);

        try
        {
            using SqliteConnection sqlite = new($"Data Source={capture.LastCapturedPath}");
            await sqlite.OpenAsync();

            using SqliteCommand cmd = new("SELECT DISTINCT TelemetryType FROM MachineTelemetry ORDER BY TelemetryType", sqlite);
            using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
            List<int> types = [];
            while (await reader.ReadAsync())
            {
                types.Add(reader.GetInt32(0));
            }

            await Assert.That(types.Count).IsEqualTo(3);
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    [Test]
    public async Task ProcessExportJobAsync_ExportMetadata_ContainsRequiredKeys()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, tenantId: 1);
        MachineTelemetry t = TestDataBuilder.BuildMachineTelemetry(machineId: machineId);
        await dbFactory.Context.InsertWithInt64IdentityAsync(t);

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data, CancellationToken.None);

        try
        {
            using SqliteConnection sqlite = new($"Data Source={capture.LastCapturedPath}");
            await sqlite.OpenAsync();

            using SqliteCommand cmd = new("SELECT Key FROM ExportMetadata ORDER BY Key", sqlite);
            using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
            List<string> keys = [];
            while (await reader.ReadAsync())
            {
                keys.Add(reader.GetString(0));
            }

            await Assert.That(keys).Contains("ExportedAt");
            await Assert.That(keys).Contains("MachineCount");
            await Assert.That(keys).Contains("Platform");
            await Assert.That(keys).Contains("SchemaVersion");
            await Assert.That(keys).Contains("TelemetryRecordCount");
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    // ========== GetExportJobAsync tests ==========

    [Test]
    public async Task GetExportJobAsync_WrongTenant_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        int jobId = createResult.Data;

        ServiceResult<DataExportJob> result = await handler.GetExportJobAsync(jobId, 999, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetExportJobAsync_CompletedJob_ReturnsObjectKey()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        int jobId = createResult.Data;
        await handler.ProcessExportJobAsync(jobId, CancellationToken.None);

        ServiceResult<DataExportJob> result = await handler.GetExportJobAsync(jobId, 1, CancellationToken.None);

        try
        {
            await Assert.That(result.IsSuccess).IsEqualTo(true);
            await Assert.That(result.Data!.Status).IsEqualTo(DataExportJobStatus.Complete);
            await Assert.That(result.Data!.ObjectKey).IsNotEqualTo(string.Empty);
            await Assert.That(result.Data!.DownloadToken).IsNotEqualTo(string.Empty);
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    // ========== Helpers ==========

    private static void CleanupFile(string? filePath)
    {
        if (filePath is not null && File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}

/// <summary>
/// Test implementation of IObjectStorageService that captures the uploaded file path.
/// </summary>
internal sealed class CaptureObjectStorageService : IObjectStorageService
{
    /// <summary>
    /// The path to the copy of the last uploaded file.
    /// </summary>
    public string? LastCapturedPath { get; private set; }

    /// <inheritdoc/>
    public Task<string> UploadFileAsync(string key, string filePath, CancellationToken ct)
    {
        string copyPath = filePath + ".testcopy";
        File.Copy(filePath, copyPath, true);
        LastCapturedPath = copyPath;

        return Task.FromResult(key);
    }

    /// <inheritdoc/>
    public Task<string> GeneratePresignedUrlAsync(string key, TimeSpan expiry)
    {
        return Task.FromResult("https://s3.example.com/fake-presigned-url");
    }

    /// <inheritdoc/>
    public Task<Stream> GetObjectStreamAsync(string key, CancellationToken ct)
    {
        return Task.FromResult<Stream>(new MemoryStream());
    }

    /// <inheritdoc/>
    public Task DeleteObjectAsync(string key, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
