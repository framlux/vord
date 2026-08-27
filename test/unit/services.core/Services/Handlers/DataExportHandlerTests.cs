// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="DataExportHandler"/>.
/// </summary>
public class DataExportHandlerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private static DataExportHandler CreateHandler(
        TestDatabaseFactory dbFactory,
        IObjectStorageService? objectStorage = null,
        ILogger<DataExportHandler>? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? exportCooldown = null)
    {
        objectStorage ??= new CaptureObjectStorageService();
        logger ??= Substitute.For<ILogger<DataExportHandler>>();
        timeProvider ??= new FakeTimeProvider(FixedNow);

        DatabaseRepository repo = new(dbFactory.Context, NullLogger<DatabaseRepository>.Instance);

        // The handler now reads the tenant subscription through the cached ISubscriptionService.
        // Delegate the mock to the real repository so tests that seed a subscription row keep working.
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => repo.GetSubscriptionForTenantAsync(callInfo.ArgAt<int>(0), callInfo.ArgAt<CancellationToken>(1)));
        subscriptionService.GetDataExportCooldownAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(exportCooldown ?? TimeSpan.FromHours(24));

        return new DataExportHandler(repo, repo, repo, repo, subscriptionService, repo, logger, objectStorage, timeProvider);
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

        ServiceResult<DataExportRequestOutcome> result = await handler.ExportTenantDataAsync(null, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task ExportTenantDataAsync_ValidTenant_CreatesJobWithPendingStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<DataExportRequestOutcome> result = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.JobId).IsGreaterThan(0);

        DataExportJob? job = await dbFactory.Context.DataExportJobs
            .FirstOrDefaultAsync(j => j.Id == result.Data!.JobId);
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

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        int jobId = createResult.Data!.JobId;
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

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data!.JobId, CancellationToken.None);

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

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data!.JobId, CancellationToken.None);

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

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data!.JobId, CancellationToken.None);

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

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data!.JobId, CancellationToken.None);

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

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data!.JobId, CancellationToken.None);

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

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data!.JobId, CancellationToken.None);

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

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        int jobId = createResult.Data!.JobId;

        ServiceResult<DataExportJob> result = await handler.GetExportJobAsync(jobId, 999, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetExportJobAsync_CompletedJob_ReturnsObjectKey()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);

        CaptureObjectStorageService capture = new();
        DataExportHandler handler = CreateHandler(dbFactory, objectStorage: capture);

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        int jobId = createResult.Data!.JobId;
        await handler.ProcessExportJobAsync(jobId, CancellationToken.None);

        ServiceResult<DataExportJob> result = await handler.GetExportJobAsync(jobId, 1, CancellationToken.None);

        try
        {
            await Assert.That(result.IsSuccess).IsTrue();
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
        ServiceResult<DataExportRequestOutcome> result = await handler.ExportTenantDataAsync(999, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task ExportTenantDataAsync_OnlyDeletedMachines_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 5, isDeleted: true);
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<DataExportRequestOutcome> result = await handler.ExportTenantDataAsync(5, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    /// <summary>
    /// A tenant that has never exported is eligible immediately.
    /// </summary>
    [Test]
    public async Task GetNextExportEligibleAtAsync_NoPriorExport_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        DataExportHandler handler = CreateHandler(dbFactory);

        DateTimeOffset? eligibleAt = await handler.GetNextExportEligibleAtAsync(1, CancellationToken.None);

        await Assert.That(eligibleAt).IsNull();
    }

    /// <summary>
    /// Inside the window the caller is told exactly when the next export becomes available, so the
    /// rejection can say something more useful than "no".
    /// </summary>
    [Test]
    public async Task GetNextExportEligibleAtAsync_WithinWindow_ReturnsWhenTheWindowExpires()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);

        DateTimeOffset lastRequest = FixedNow.AddHours(-2);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildDataExportJob(
            tenantId: 1, requestedByUserId: 1, status: DataExportJobStatus.Complete, requestedAt: lastRequest));

        DataExportHandler handler = CreateHandler(dbFactory, exportCooldown: TimeSpan.FromHours(24));

        DateTimeOffset? eligibleAt = await handler.GetNextExportEligibleAtAsync(1, CancellationToken.None);

        await Assert.That(eligibleAt).IsEqualTo(lastRequest.AddHours(24));
    }

    /// <summary>
    /// Once the window has elapsed the tenant is eligible again.
    /// </summary>
    [Test]
    public async Task GetNextExportEligibleAtAsync_WindowElapsed_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildDataExportJob(
            tenantId: 1, requestedByUserId: 1, status: DataExportJobStatus.Complete,
            requestedAt: FixedNow.AddHours(-25)));

        DataExportHandler handler = CreateHandler(dbFactory, exportCooldown: TimeSpan.FromHours(24));

        DateTimeOffset? eligibleAt = await handler.GetNextExportEligibleAtAsync(1, CancellationToken.None);

        await Assert.That(eligibleAt).IsNull();
    }

    /// <summary>
    /// A failed export still consumed the work the cooldown rations, so it holds the window shut.
    /// </summary>
    [Test]
    public async Task GetNextExportEligibleAtAsync_PriorExportFailed_StillHoldsTheWindow()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);

        DateTimeOffset lastRequest = FixedNow.AddHours(-1);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildDataExportJob(
            tenantId: 1, requestedByUserId: 1, status: DataExportJobStatus.Failed, requestedAt: lastRequest));

        DataExportHandler handler = CreateHandler(dbFactory, exportCooldown: TimeSpan.FromHours(12));

        DateTimeOffset? eligibleAt = await handler.GetNextExportEligibleAtAsync(1, CancellationToken.None);

        await Assert.That(eligibleAt).IsEqualTo(lastRequest.AddHours(12));
    }

    /// <summary>
    /// A zero cooldown — what a self-hosted deployment reports — never withholds an export, even
    /// when one was requested moments ago.
    /// </summary>
    [Test]
    public async Task GetNextExportEligibleAtAsync_ZeroCooldown_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildDataExportJob(
            tenantId: 1, requestedByUserId: 1, status: DataExportJobStatus.Complete,
            requestedAt: FixedNow.AddMinutes(-1)));

        DataExportHandler handler = CreateHandler(dbFactory, exportCooldown: TimeSpan.Zero);

        DateTimeOffset? eligibleAt = await handler.GetNextExportEligibleAtAsync(1, CancellationToken.None);

        await Assert.That(eligibleAt).IsNull();
    }

    [Test]
    public async Task ExportTenantDataAsync_ActiveJobExists_Returns409()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);
        DataExportHandler handler = CreateHandler(dbFactory);

        // Create first job
        ServiceResult<DataExportRequestOutcome> firstResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await Assert.That(firstResult.IsSuccess).IsTrue();

        // Attempt second job while first is still pending
        ServiceResult<DataExportRequestOutcome> secondResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);

        await Assert.That(secondResult.StatusCode).IsEqualTo(409);
    }

    [Test]
    public async Task ExportTenantDataAsync_CompletedJobExists_AllowsNewJob()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);

        CaptureObjectStorageService capture = new();

        // A completed job no longer blocks on its own; the tier cooldown is what decides. This
        // case is the one where the window has already elapsed.
        FakeTimeProvider clock = new(FixedNow);
        DataExportHandler handler = CreateHandler(
            dbFactory, objectStorage: capture, timeProvider: clock, exportCooldown: TimeSpan.FromHours(24));

        // Create and process first job to completion
        ServiceResult<DataExportRequestOutcome> firstResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(firstResult.Data!.JobId, CancellationToken.None);

        try
        {
            clock.Advance(TimeSpan.FromHours(25));

            // Second job should succeed since first is completed and the window has passed
            ServiceResult<DataExportRequestOutcome> secondResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);

            await Assert.That(secondResult.IsSuccess).IsTrue();
            await Assert.That(secondResult.Data!.JobId).IsGreaterThan(firstResult.Data!.JobId);
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    /// <summary>
    /// The counterpart: a completed export inside the tier window is refused with 429 rather than
    /// silently allowed. This is the hole the cooldown exists to close — without it a tenant can
    /// re-request a full-database export the instant the previous one finishes, in a loop.
    /// </summary>
    [Test]
    public async Task ExportTenantDataAsync_WithinCooldownWindow_Returns429()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);

        CaptureObjectStorageService capture = new();
        FakeTimeProvider clock = new(FixedNow);
        DataExportHandler handler = CreateHandler(
            dbFactory, objectStorage: capture, timeProvider: clock, exportCooldown: TimeSpan.FromHours(24));

        ServiceResult<DataExportRequestOutcome> firstResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(firstResult.Data!.JobId, CancellationToken.None);

        try
        {
            clock.Advance(TimeSpan.FromHours(23));

            ServiceResult<DataExportRequestOutcome> secondResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);

            await Assert.That(secondResult.StatusCode).IsEqualTo(429);
        }
        finally
        {
            CleanupFile(capture.LastCapturedPath);
        }
    }

    /// <summary>
    /// A refused export is auditable. Without this the only trace of a throttled tenant is a log
    /// line, which is not something an operator can answer a support question from.
    /// </summary>
    [Test]
    public async Task ExportTenantDataAsync_WithinCooldownWindow_WritesThrottledAuditEntry()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);

        CaptureObjectStorageService capture = new();
        FakeTimeProvider clock = new(FixedNow);
        DataExportHandler handler = CreateHandler(
            dbFactory, objectStorage: capture, timeProvider: clock, exportCooldown: TimeSpan.FromHours(24));

        ServiceResult<DataExportRequestOutcome> firstResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(firstResult.Data!.JobId, CancellationToken.None);

        try
        {
            clock.Advance(TimeSpan.FromHours(1));

            await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);

            List<AuditLogEntry> throttled = await dbFactory.Context.AuditLog
                .Where(a => a.Action == AuditAction.DataExportThrottled)
                .ToListAsync();

            await Assert.That(throttled.Count).IsEqualTo(1);
            await Assert.That(throttled[0].TenantId).IsEqualTo(1);
            await Assert.That(throttled[0].UserId).IsEqualTo(1);
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

        ServiceResult<DataExportRequestOutcome> result = await handler.ExportTenantDataAsync(1, 42, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        AuditLogEntry? audit = await dbFactory.Context.AuditLog
            .FirstOrDefaultAsync(a => a.ResourceId == result.Data!.JobId.ToString());
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

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        int jobId = createResult.Data!.JobId;

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
        await Assert.That(string.IsNullOrEmpty(job.ErrorMessage)).IsFalse();
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

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data!.JobId, CancellationToken.None);

        DataExportJob? job = await dbFactory.Context.DataExportJobs
            .FirstOrDefaultAsync(j => j.Id == createResult.Data!.JobId);
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

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data!.JobId, CancellationToken.None);

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

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        await handler.ProcessExportJobAsync(createResult.Data!.JobId, CancellationToken.None);

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

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetExportJobAsync_NonExistentJob_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<DataExportJob> result = await handler.GetExportJobAsync(99999, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetExportJobAsync_CorrectTenant_ReturnsJob()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        int jobId = createResult.Data!.JobId;

        ServiceResult<DataExportJob> result = await handler.GetExportJobAsync(jobId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
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

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetExportJobByTokenAsync_EmptyToken_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<DataExportJob> result = await handler.GetExportJobByTokenAsync(string.Empty, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetExportJobByTokenAsync_NonExistentToken_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<DataExportJob> result = await handler.GetExportJobByTokenAsync("nonexistent-token", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetExportJobByTokenAsync_ValidToken_ReturnsJob()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1);
        DataExportHandler handler = CreateHandler(dbFactory);

        ServiceResult<DataExportRequestOutcome> createResult = await handler.ExportTenantDataAsync(1, 1, CancellationToken.None);
        int jobId = createResult.Data!.JobId;

        // Retrieve the job to get its token
        DataExportJob? createdJob = await dbFactory.Context.DataExportJobs
            .FirstOrDefaultAsync(j => j.Id == jobId);
        await Assert.That(createdJob).IsNotNull();

        ServiceResult<DataExportJob> result = await handler.GetExportJobByTokenAsync(
            createdJob!.DownloadToken, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
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
