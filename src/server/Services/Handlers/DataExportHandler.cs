// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations.Export;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Services.DataExport;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Exports all machine-generated telemetry data for a tenant into a portable SQLite database.
/// Supports asynchronous job-based processing with S3 upload.
/// </summary>
public sealed class DataExportHandler : IDataExportHandler
{
    private readonly DatabaseContext _db;
    private readonly ILogger<DataExportHandler> _logger;
    private readonly IObjectStorageService _objectStorageService;

    private const int BatchSize = 5000;

    /// <summary>
    /// Creates a new instance of the <see cref="DataExportHandler"/> class.
    /// </summary>
    public DataExportHandler(
        DatabaseContext db,
        ILogger<DataExportHandler> logger,
        IObjectStorageService objectStorageService)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(objectStorageService);

        _db = db;
        _logger = logger;
        _objectStorageService = objectStorageService;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<int>> ExportTenantDataAsync(int? tenantId, int requestedByUserId, CancellationToken ct)
    {
        if (tenantId is null)
        {

            return ServiceResult<int>.NotFound();
        }

        // Check if there are machines to export
        int machineCount = await _db.Machines
            .Where(m => m.TenantId == tenantId.Value && m.IsDeleted == false)
            .CountAsync(ct);

        if (machineCount == 0)
        {

            return ServiceResult<int>.NotFound();
        }

        // Reject if tenant already has a Pending or Processing job
        bool hasActiveJob = await _db.DataExportJobs
            .AnyAsync(j => j.TenantId == tenantId.Value &&
                          (j.Status == DataExportJobStatus.Pending || j.Status == DataExportJobStatus.Processing), ct);

        if (hasActiveJob)
        {

            return ServiceResult<int>.Error(409, 0);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        DataExportJob job = new()
        {
            TenantId = tenantId.Value,
            Status = DataExportJobStatus.Pending,
            RequestedByUserId = requestedByUserId,
            RequestedAt = now,
            ObjectKey = "",
            ExpiresAt = now.AddDays(7),
            DownloadToken = RandomNumberGenerator.GetHexString(64, true)
        };

        using DataConnectionTransaction transaction = await _db.BeginTransactionAsync(ct);

        job.Id = await _db.InsertWithInt32IdentityAsync(job, token: ct);

        await _db.InsertAsync(AuditHelper.Create(
            tenantId, requestedByUserId, null,
            AuditAction.DataExportRequested, AuditResourceType.DataExport,
            job.Id.ToString(), null, null), token: ct);

        await transaction.CommitAsync(ct);

        _logger.LogInformation("Created data export job {JobId} for tenant {TenantId}", job.Id, tenantId);

        return ServiceResult<int>.Ok(job.Id);
    }

    /// <inheritdoc/>
    public async Task ProcessExportJobAsync(int jobId, CancellationToken ct)
    {

        DataExportJob? job = await _db.DataExportJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null)
        {
            _logger.LogWarning("Export job {JobId} not found", jobId);

            return;
        }

        // Update status to Processing
        await _db.DataExportJobs
            .Where(j => j.Id == jobId)
            .Set(j => j.Status, DataExportJobStatus.Processing)
            .UpdateAsync(ct);

        string tempPath = Path.Combine(Path.GetTempPath(), $"vord-export-{job.TenantId}-{Guid.NewGuid():N}.sqlite");

        try
        {
            List<long> machineIds = await _db.Machines
                .Where(m => m.TenantId == job.TenantId && m.IsDeleted == false)
                .Select(m => m.Id)
                .ToListAsync(ct);

            if (machineIds.Count == 0)
            {
                await FailJobAsync(jobId, "No machines found for tenant", ct);

                return;
            }

            string sqliteConnectionString = $"Data Source={tempPath}";
            CreateExportSchema(sqliteConnectionString);

            using SqliteConnection sqlite = new(sqliteConnectionString);
            await sqlite.OpenAsync(ct);

            await ExportMachinesAsync(sqlite, job.TenantId, ct);
            await ExportMachineStateAsync(sqlite, machineIds, ct);
            await ExportTelemetryAsync(sqlite, machineIds, ct);

            // Include audit log for Team tier subscriptions
            TenantSubscription? subscription = await _db.TenantSubscriptions
                .FirstOrDefaultAsync(s => s.TenantId == job.TenantId, ct);

            if (subscription is not null && subscription.Tier == SubscriptionTier.Team)
            {
                await ExportAuditLogAsync(sqlite, job.TenantId, ct);
            }

            sqlite.Close();

            // Upload to S3
            long fileSize = new FileInfo(tempPath).Length;
            string objectKey = $"exports/tenant-{job.TenantId}/{job.Id}/vord-export-{DateTimeOffset.UtcNow:yyyy-MM-dd}.sqlite";
            await _objectStorageService.UploadFileAsync(objectKey, tempPath, ct);

            // Update job to Complete
            await _db.DataExportJobs
                .Where(j => j.Id == jobId)
                .Set(j => j.Status, DataExportJobStatus.Complete)
                .Set(j => j.CompletedAt, DateTimeOffset.UtcNow)
                .Set(j => j.ObjectKey, objectKey)
                .Set(j => j.FileSizeBytes, fileSize)
                .UpdateAsync(ct);

            _logger.LogInformation("Data export job {JobId} completed for tenant {TenantId}. Size: {Size} bytes",
                jobId, job.TenantId, fileSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Data export job {JobId} failed for tenant {TenantId}", jobId, job.TenantId);
            await FailJobAsync(jobId, ex.Message, ct);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<DataExportJob>> GetExportJobAsync(int jobId, int? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {

            return ServiceResult<DataExportJob>.NotFound();
        }

        DataExportJob? job = await _db.DataExportJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.TenantId == tenantId.Value, ct);

        if (job is null)
        {

            return ServiceResult<DataExportJob>.NotFound();
        }

        return ServiceResult<DataExportJob>.Ok(job);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<DataExportJob>> GetExportJobByTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
        {

            return ServiceResult<DataExportJob>.NotFound();
        }

        DataExportJob? job = await _db.DataExportJobs
            .FirstOrDefaultAsync(j => j.DownloadToken == token, ct);

        if (job is null)
        {

            return ServiceResult<DataExportJob>.NotFound();
        }

        return ServiceResult<DataExportJob>.Ok(job);
    }

    private async Task ExportAuditLogAsync(
        SqliteConnection sqlite, int tenantId, CancellationToken ct)
    {
        using SqliteTransaction tx = sqlite.BeginTransaction();
        using SqliteCommand cmd = sqlite.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO AuditLog (Id, UserId, MachineId, Action, ResourceType, ResourceId, Details, IpAddress, Timestamp)
            VALUES ($id, $userId, $machineId, $action, $resourceType, $resourceId, $details, $ipAddress, $timestamp)
            """;

        SqliteParameter pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        SqliteParameter pUserId = cmd.Parameters.Add("$userId", SqliteType.Integer);
        SqliteParameter pMachineId = cmd.Parameters.Add("$machineId", SqliteType.Integer);
        SqliteParameter pAction = cmd.Parameters.Add("$action", SqliteType.Integer);
        SqliteParameter pResourceType = cmd.Parameters.Add("$resourceType", SqliteType.Integer);
        SqliteParameter pResourceId = cmd.Parameters.Add("$resourceId", SqliteType.Text);
        SqliteParameter pDetails = cmd.Parameters.Add("$details", SqliteType.Text);
        SqliteParameter pIpAddress = cmd.Parameters.Add("$ipAddress", SqliteType.Text);
        SqliteParameter pTimestamp = cmd.Parameters.Add("$timestamp", SqliteType.Text);

        long totalRows = 0;
        long lastId = 0;

        while (true)
        {
            long capturedLastId = lastId;
            List<AuditLogEntry> batch = await _db.AuditLog
                .Where(a => a.TenantId == tenantId && a.Id > capturedLastId)
                .OrderBy(a => a.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (AuditLogEntry a in batch)
            {
                pId.Value = a.Id;
                pUserId.Value = (object?)a.UserId ?? DBNull.Value;
                pMachineId.Value = (object?)a.MachineId ?? DBNull.Value;
                pAction.Value = (int)a.Action;
                pResourceType.Value = (int)a.ResourceType;
                pResourceId.Value = (object?)a.ResourceId ?? DBNull.Value;
                pDetails.Value = (object?)a.Details ?? DBNull.Value;
                pIpAddress.Value = (object?)a.IpAddress ?? DBNull.Value;
                pTimestamp.Value = a.Timestamp.ToString("o");

                await cmd.ExecuteNonQueryAsync(ct);
            }

            totalRows += batch.Count;
            lastId = batch[^1].Id;

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        // Update metadata with audit log count
        using SqliteCommand metaCmd = sqlite.CreateCommand();
        metaCmd.Transaction = tx;
        metaCmd.CommandText = "INSERT INTO ExportMetadata (Key, Value) VALUES ($key, $value)";
        metaCmd.Parameters.AddWithValue("$key", "AuditLogRecordCount");
        metaCmd.Parameters.AddWithValue("$value", totalRows.ToString());
        await metaCmd.ExecuteNonQueryAsync(ct);

        tx.Commit();
    }

    private static void CreateExportSchema(string connectionString)
    {
        using SqliteConnection sqlite = new(connectionString);
        sqlite.Open();
        using SqliteCommand cmd = sqlite.CreateCommand();
        cmd.CommandText = ExportSchemaSql.CreateSchema;
        cmd.ExecuteNonQuery();
    }

    private async Task FailJobAsync(int jobId, string errorMessage, CancellationToken ct)
    {
        await _db.DataExportJobs
            .Where(j => j.Id == jobId)
            .Set(j => j.Status, DataExportJobStatus.Failed)
            .Set(j => j.CompletedAt, DateTimeOffset.UtcNow)
            .Set(j => j.ErrorMessage, errorMessage)
            .UpdateAsync(ct);
    }

    private async Task ExportMachinesAsync(
        SqliteConnection sqlite, int tenantId, CancellationToken ct)
    {
        List<Machine> machines = await _db.Machines
            .Where(m => m.TenantId == tenantId && m.IsDeleted == false)
            .ToListAsync(ct);

        using SqliteTransaction tx = sqlite.BeginTransaction();
        using SqliteCommand cmd = sqlite.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Machines (Id, Name, Description, Location, SerialNumber, SystemId, AssetTagNumber, MachineType, OperatingSystem, RegisteredOn)
            VALUES ($id, $name, $desc, $loc, $serial, $sysid, $asset, $type, $os, $reg)
            """;

        SqliteParameter pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        SqliteParameter pName = cmd.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter pDesc = cmd.Parameters.Add("$desc", SqliteType.Text);
        SqliteParameter pLoc = cmd.Parameters.Add("$loc", SqliteType.Text);
        SqliteParameter pSerial = cmd.Parameters.Add("$serial", SqliteType.Text);
        SqliteParameter pSysId = cmd.Parameters.Add("$sysid", SqliteType.Text);
        SqliteParameter pAsset = cmd.Parameters.Add("$asset", SqliteType.Text);
        SqliteParameter pType = cmd.Parameters.Add("$type", SqliteType.Integer);
        SqliteParameter pOs = cmd.Parameters.Add("$os", SqliteType.Integer);
        SqliteParameter pReg = cmd.Parameters.Add("$reg", SqliteType.Text);

        foreach (Machine m in machines)
        {
            pId.Value = m.Id;
            pName.Value = m.Name;
            pDesc.Value = (object?)m.Description ?? DBNull.Value;
            pLoc.Value = (object?)m.Location ?? DBNull.Value;
            pSerial.Value = m.SerialNumber;
            pSysId.Value = m.SystemId;
            pAsset.Value = (object?)m.AssetTagNumber ?? DBNull.Value;
            pType.Value = (int)m.MachineType;
            pOs.Value = (int)m.OperatingSystem;
            pReg.Value = m.RegisteredOn.ToString("o");

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Write export metadata
        using SqliteCommand metaCmd = sqlite.CreateCommand();
        metaCmd.Transaction = tx;
        metaCmd.CommandText = "INSERT INTO ExportMetadata (Key, Value) VALUES ($key, $value)";
        SqliteParameter pKey = metaCmd.Parameters.Add("$key", SqliteType.Text);
        SqliteParameter pValue = metaCmd.Parameters.Add("$value", SqliteType.Text);

        pKey.Value = "ExportedAt";
        pValue.Value = DateTimeOffset.UtcNow.ToString("o");
        await metaCmd.ExecuteNonQueryAsync(ct);

        pKey.Value = "MachineCount";
        pValue.Value = machines.Count.ToString();
        await metaCmd.ExecuteNonQueryAsync(ct);

        pKey.Value = "Platform";
        pValue.Value = "Vord by Framlux";
        await metaCmd.ExecuteNonQueryAsync(ct);

        pKey.Value = "SchemaVersion";
        pValue.Value = "1";
        await metaCmd.ExecuteNonQueryAsync(ct);

        tx.Commit();
    }

    private async Task ExportMachineStateAsync(
        SqliteConnection sqlite, List<long> machineIds, CancellationToken ct)
    {
        List<MachineStateSummary> states = await _db.MachineStateSummaries
            .Where(s => machineIds.Contains(s.MachineId))
            .ToListAsync(ct);

        using SqliteTransaction tx = sqlite.BeginTransaction();
        using SqliteCommand cmd = sqlite.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO MachineStateSummary (
                MachineId, TenantId, Name, OperatingSystem, MachineType,
                Hostname, HardwareModel, IpAddresses,
                OsName, OsVersion, CpuUsagePercent, MemoryUsagePercent,
                MaxDiskUsagePercent, PendingUpdates, SecurityUpdates,
                TotalServices, FailedServices, HasDiskHealthIssue,
                HasHardwareIssue, HealthStatus, LastSeenAt
            ) VALUES (
                $MachineId, $TenantId, $Name, $OperatingSystem, $MachineType,
                $Hostname, $HardwareModel, $IpAddresses,
                $OsName, $OsVersion, $CpuUsagePercent, $MemoryUsagePercent,
                $MaxDiskUsagePercent, $PendingUpdates, $SecurityUpdates,
                $TotalServices, $FailedServices, $HasDiskHealthIssue,
                $HasHardwareIssue, $HealthStatus, $LastSeenAt
            )
            """;

        foreach (MachineStateSummary s in states)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$MachineId", s.MachineId);
            cmd.Parameters.AddWithValue("$TenantId", s.TenantId);
            cmd.Parameters.AddWithValue("$Name", s.Name);
            cmd.Parameters.AddWithValue("$OperatingSystem", s.OperatingSystem);
            cmd.Parameters.AddWithValue("$MachineType", s.MachineType);
            cmd.Parameters.AddWithValue("$Hostname", (object?)s.Hostname ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$HardwareModel", (object?)s.HardwareModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$IpAddresses", (object?)s.IpAddresses ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$OsName", (object?)s.OsName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$OsVersion", (object?)s.OsVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$CpuUsagePercent", (object?)s.CpuUsagePercent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$MemoryUsagePercent", (object?)s.MemoryUsagePercent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$MaxDiskUsagePercent", (object?)s.MaxDiskUsagePercent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$PendingUpdates", (object?)s.PendingUpdates ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$SecurityUpdates", (object?)s.SecurityUpdates ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$TotalServices", (object?)s.TotalServices ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$FailedServices", (object?)s.FailedServices ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$HasDiskHealthIssue", (object?)s.HasDiskHealthIssue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$HasHardwareIssue", (object?)s.HasHardwareIssue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$HealthStatus", s.HealthStatus);
            cmd.Parameters.AddWithValue("$LastSeenAt", (object?)s.LastSeenAt?.ToString("o") ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
    }

    private async Task ExportTelemetryAsync(
        SqliteConnection sqlite, List<long> machineIds, CancellationToken ct)
    {
        using SqliteTransaction tx = sqlite.BeginTransaction();
        using SqliteCommand cmd = sqlite.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO MachineTelemetry (Id, MachineId, TelemetryType, Payload, ReceivedAt, SourceEventId)
            VALUES ($id, $mid, $type, $payload, $received, $eventid)
            """;

        SqliteParameter pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        SqliteParameter pMid = cmd.Parameters.Add("$mid", SqliteType.Integer);
        SqliteParameter pType = cmd.Parameters.Add("$type", SqliteType.Integer);
        SqliteParameter pPayload = cmd.Parameters.Add("$payload", SqliteType.Text);
        SqliteParameter pReceived = cmd.Parameters.Add("$received", SqliteType.Text);
        SqliteParameter pEventId = cmd.Parameters.Add("$eventid", SqliteType.Text);

        long totalRows = 0;
        long lastId = 0;

        while (true)
        {
            long capturedLastId = lastId;
            List<MachineTelemetry> batch = await _db.MachineTelemetry
                .Where(t => machineIds.Contains(t.MachineId) &&
                            t.Id > capturedLastId)
                .OrderBy(t => t.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (MachineTelemetry t in batch)
            {
                pId.Value = t.Id;
                pMid.Value = t.MachineId;
                pType.Value = t.TelemetryType;
                pPayload.Value = t.Payload;
                pReceived.Value = t.ReceivedAt.ToString("o");
                pEventId.Value = (object?)t.SourceEventId ?? DBNull.Value;

                await cmd.ExecuteNonQueryAsync(ct);
            }

            totalRows += batch.Count;
            lastId = batch[^1].Id;

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        // Update metadata with telemetry count
        using SqliteCommand metaCmd = sqlite.CreateCommand();
        metaCmd.Transaction = tx;
        metaCmd.CommandText = "INSERT INTO ExportMetadata (Key, Value) VALUES ($key, $value)";
        metaCmd.Parameters.AddWithValue("$key", "TelemetryRecordCount");
        metaCmd.Parameters.AddWithValue("$value", totalRows.ToString());
        await metaCmd.ExecuteNonQueryAsync(ct);

        tx.Commit();
    }
}
