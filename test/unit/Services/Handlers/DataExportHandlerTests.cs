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
using NSubstitute.ExceptionExtensions;

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

    // ========== ExportTenantDataAsync edge cases ==========

    [Test]
    public async Task ExportTenantDataAsync_NoMachinesForTenant_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        DataExportHandler handler = CreateHandler(dbFactory);

        // Tenant 999 has no machines
        ServiceResult<int> result = await handler.ExportTenantDataAsync(999, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task ExportTenantDataAsync_OnlyDeletedMachines_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 5, isDeleted: true);
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<int> result = await handler.ExportTenantDataAsync(5, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task ExportTenantDataAsync_ActiveJobExists_Returns409()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);
        DataExportHandler handler = CreateHandler(dbFactory);

        // Create first job
        ServiceResult<int> firstResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await Assert.That(firstResult.IsSuccess).IsEqualTo(true);

        // Attempt second job while first is still pending
        ServiceResult<int> secondResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);

        await Assert.That(secondResult.StatusCode).IsEqualTo(409);
    }

    [Test]
    public async Task ExportTenantDataAsync_CompletedJobExists_AllowsNewJob()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        // Create and process first job to completion
        ServiceResult<int> firstResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(firstResult.Data, CancellationToken.None);

        try
        {
            // Second job should succeed since first is completed
            ServiceResult<int> secondResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);

            await Assert.That(secondResult.IsSuccess).IsEqualTo(true);
            await Assert.That(secondResult.Data).IsGreaterThan(firstResult.Data);
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    [Test]
    public async Task ExportTenantDataAsync_CreatesAuditLogEntry()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<int> result = await handler.ExportTenantDataAsync(1, 42, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);

        AuditLogEntry? audit = await dbFactory.Context.AuditLog
            .FirstOrDefaultAsync(a => a.ResourceId == result.Data.ToString());
        await Assert.That(audit).IsNotNull();
        await Assert.That(audit!.Action).IsEqualTo(AuditAction.DataExportRequested);
    }

    // ========== ProcessExportJobAsync edge cases ==========

    [Test]
    public async Task ProcessExportJobAsync_NonExistentJob_DoesNotThrow()
    {
        using TestDatabaseFactory dbFactory = new();
        DataExportHandler handler = CreateHandler(dbFactory);

        // Should not throw; logs warning and returns
        await handler.ProcessExportJobAsync(99999, CancellationToken.None);
    }

    [Test]
    public async Task ProcessExportJobAsync_NoMachinesAfterJobCreated_FailsJob()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, tenantId: 1);
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        int jobId = createResult.Data;

        // Delete all machines after job was created
        await dbFactory.Context.Machines
            .Where(m => m.Id == machineId)
            .Set(m => m.IsDeleted, true)
            .UpdateAsync();

        await handler.ProcessExportJobAsync(jobId, CancellationToken.None);

        DataExportJob? job = await dbFactory.Context.DataExportJobs
            .FirstOrDefaultAsync(j => j.Id == jobId);
        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Status).IsEqualTo(DataExportJobStatus.Failed);
        await Assert.That(string.IsNullOrEmpty(job.ErrorMessage)).IsEqualTo(false);
    }

    [Test]
    public async Task ProcessExportJobAsync_UploadFails_FailsJob()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);

        IObjectStorageService failingStorage = Substitute.For<IObjectStorageService>();
        failingStorage.UploadFileAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Upload failed"));

        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: failingStorage);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data, CancellationToken.None);

        DataExportJob? job = await dbFactory.Context.DataExportJobs
            .FirstOrDefaultAsync(j => j.Id == createResult.Data);
        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Status).IsEqualTo(DataExportJobStatus.Failed);
        await Assert.That(job.ErrorMessage).IsNotNull();
    }

    [Test]
    public async Task ProcessExportJobAsync_TeamTierSubscription_IncludesAuditLog()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, tenantId: 1);

        // Seed a Team tier subscription
        TenantSubscription subscription = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Team, status: SubscriptionStatus.Active);
        await dbFactory.Context.InsertWithInt32IdentityAsync(subscription);

        // Seed an audit log entry
        AuditLogEntry auditEntry = new()
        {
            TenantId = 1,
            UserId = 1,
            MachineId = null,
            Action = AuditAction.DataExportRequested,
            ResourceType = AuditResourceType.DataExport,
            ResourceId = "test",
            Details = null,
            IpAddress = null,
            Timestamp = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt64IdentityAsync(auditEntry);

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data, CancellationToken.None);

        try
        {
            await Assert.That(capture.LastCapturedPath).IsNotNull();

            using SqliteConnection sqlite = new($"Data Source={capture.LastCapturedPath}");
            await sqlite.OpenAsync();

            // Verify audit log table has data
            using SqliteCommand cmd = new("SELECT COUNT(*) FROM AuditLog", sqlite);
            long auditCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            await Assert.That(auditCount).IsGreaterThanOrEqualTo(1);

            // Verify metadata includes AuditLogRecordCount
            using SqliteCommand metaCmd = new(
                "SELECT Value FROM ExportMetadata WHERE Key = 'AuditLogRecordCount'", sqlite);
            string? auditMeta = (string?)await metaCmd.ExecuteScalarAsync();
            await Assert.That(auditMeta).IsNotNull();
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    [Test]
    public async Task ProcessExportJobAsync_NonTeamTierSubscription_ExcludesAuditLog()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);

        // Seed a Pro tier subscription (not Team)
        TenantSubscription subscription = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        await dbFactory.Context.InsertWithInt32IdentityAsync(subscription);

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data, CancellationToken.None);

        try
        {
            using SqliteConnection sqlite = new($"Data Source={capture.LastCapturedPath}");
            await sqlite.OpenAsync();

            // Verify no AuditLogRecordCount metadata key
            using SqliteCommand metaCmd = new(
                "SELECT Value FROM ExportMetadata WHERE Key = 'AuditLogRecordCount'", sqlite);
            object? auditMeta = await metaCmd.ExecuteScalarAsync();
            await Assert.That(auditMeta).IsNull();
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    // ========== GetExportJobAsync edge cases ==========

    [Test]
    public async Task GetExportJobAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<DataExportJob> result = await handler.GetExportJobAsync(1, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetExportJobAsync_NonExistentJob_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<DataExportJob> result = await handler.GetExportJobAsync(99999, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetExportJobAsync_CorrectTenant_ReturnsJob()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        int jobId = createResult.Data;

        ServiceResult<DataExportJob> result = await handler.GetExportJobAsync(jobId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data).IsNotNull();
        await Assert.That(result.Data!.Id).IsEqualTo(jobId);
        await Assert.That(result.Data.TenantId).IsEqualTo(1);
    }

    // ========== GetExportJobByTokenAsync tests ==========

    [Test]
    public async Task GetExportJobByTokenAsync_NullToken_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<DataExportJob> result = await handler.GetExportJobByTokenAsync(null!, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetExportJobByTokenAsync_EmptyToken_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<DataExportJob> result = await handler.GetExportJobByTokenAsync(string.Empty, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetExportJobByTokenAsync_NonExistentToken_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<DataExportJob> result = await handler.GetExportJobByTokenAsync("nonexistent-token", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetExportJobByTokenAsync_ValidToken_ReturnsJob()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<int> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        int jobId = createResult.Data;

        // Retrieve the job to get its token
        DataExportJob? createdJob = await dbFactory.Context.DataExportJobs
            .FirstOrDefaultAsync(j => j.Id == jobId);
        await Assert.That(createdJob).IsNotNull();

        ServiceResult<DataExportJob> result = await handler.GetExportJobByTokenAsync(
            createdJob!.DownloadToken, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data).IsNotNull();
        await Assert.That(result.Data!.Id).IsEqualTo(jobId);
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
