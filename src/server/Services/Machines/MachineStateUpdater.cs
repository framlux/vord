// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Telemetry;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Performs lock-free per-type partial UPSERTs on MachineState via the configured SQL dialect.
/// Health status is computed at read time by <see cref="HealthComputer"/>.
/// </summary>
public sealed class MachineStateUpdater : IMachineStateUpdater
{

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MachineStateUpdater> _logger;
    private readonly ISqlDialect _dialect;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineStateUpdater"/> class.
    /// </summary>
    public MachineStateUpdater(IServiceScopeFactory scopeFactory, ILogger<MachineStateUpdater> logger, ISqlDialect dialect)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _dialect = dialect;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(long machineId, short telemetryType, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        try
        {
            // Create scope once for the entire update operation.
            using IServiceScope scope = _scopeFactory.CreateScope();
            DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

            switch (telemetryType)
            {
                case TelemetryTypeIds.SystemInfo:
                    await UpsertSystemInfoAsync(db, machineId, payload, receivedAt, ct);
                    break;
                case TelemetryTypeIds.OsVersion:
                    await UpsertOsVersionAsync(db, machineId, payload, receivedAt, ct);
                    break;
                case TelemetryTypeIds.CpuInfo:
                    await UpsertCpuInfoAsync(db, machineId, payload, receivedAt, ct);
                    break;
                case TelemetryTypeIds.MemoryInfo:
                    await UpsertMemoryInfoAsync(db, machineId, payload, receivedAt, ct);
                    break;
                case TelemetryTypeIds.DiskInfo:
                    await UpsertDiskInfoAsync(db, machineId, payload, receivedAt, ct);
                    break;
                case TelemetryTypeIds.CpuUsage:
                    await UpsertCpuUsageAsync(db, machineId, payload, receivedAt, ct);
                    break;
                case TelemetryTypeIds.MemoryUsage:
                    await UpsertMemoryUsageAsync(db, machineId, payload, receivedAt, ct);
                    break;
                case TelemetryTypeIds.DiskUsage:
                    await UpsertDiskUsageAsync(db, machineId, payload, receivedAt, ct);
                    break;
                case TelemetryTypeIds.SshSessions:
                    await UpsertSshSessionsAsync(db, machineId, payload, receivedAt, ct);
                    break;
                case TelemetryTypeIds.HardwareHealth:
                    await UpsertHardwareHealthAsync(db, machineId, payload, receivedAt, ct);
                    break;
                case TelemetryTypeIds.PackageUpdates:
                    await UpsertPackageUpdatesAsync(db, machineId, payload, receivedAt, ct);
                    break;
                case TelemetryTypeIds.ServiceStatus:
                    await UpsertServiceStatusAsync(db, machineId, payload, receivedAt, ct);
                    break;
                default:
                    await UpsertLastTelemetryAsync(db, machineId, receivedAt, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update MachineState for machine {MachineId} type {Type}", machineId, telemetryType);

            throw;
        }
    }

    private async Task UpsertSystemInfoAsync(DatabaseContext db, long machineId, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        SystemInfoPayload? info = Deserialize<SystemInfoPayload>(payload);
        if (info is null)
        {
            return;
        }

        string? ipJson = info.IpAddresses.Count > 0
            ? JsonSerializer.Serialize(info.IpAddresses, JsonDefaults.SnakeCase)
            : null;

        await db.ExecuteAsync(
            _dialect.UpsertSystemInfo,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("hostname", info.Hostname),
            new DataParameter("vendor", info.HardwareVendor),
            new DataParameter("model", info.HardwareModel),
            new DataParameter("serial", info.HardwareSerial),
            new DataParameter("cpuBrand", info.CpuBrand),
            new DataParameter("cores", info.CpuPhysicalCores),
            new DataParameter("memory", info.PhysicalMemory),
            new DataParameter("uptime", info.UptimeSeconds),
            new DataParameter("bios", info.BiosVersion),
            new DataParameter("ips", ipJson),
            new DataParameter("ts", receivedAt));
    }

    private async Task UpsertOsVersionAsync(DatabaseContext db, long machineId, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        OsVersionPayload? info = Deserialize<OsVersionPayload>(payload);
        if (info is null)
        {
            return;
        }

        await db.ExecuteAsync(
            _dialect.UpsertOsVersion,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("osName", info.Name),
            new DataParameter("osVersion", info.Version),
            new DataParameter("kernel", info.Build),
            new DataParameter("ts", receivedAt));
    }

    private async Task UpsertCpuUsageAsync(DatabaseContext db, long machineId, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        CpuUsagePayload? info = Deserialize<CpuUsagePayload>(payload);
        if (info is null)
        {
            return;
        }

        await db.ExecuteAsync(
            _dialect.UpsertCpuUsage,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("cpu", info.CpuUsagePercent),
            new DataParameter("ts", receivedAt));
    }

    private async Task UpsertMemoryUsageAsync(DatabaseContext db, long machineId, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        MemoryUsagePayload? info = Deserialize<MemoryUsagePayload>(payload);
        if (info is null)
        {
            return;
        }

        await db.ExecuteAsync(
            _dialect.UpsertMemoryUsage,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("memUsed", info.MemoryUsed),
            new DataParameter("memPct", info.MemoryUsagePercent),
            new DataParameter("ts", receivedAt));
    }

    private async Task UpsertDiskUsageAsync(DatabaseContext db, long machineId, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        await db.ExecuteAsync(
            _dialect.UpsertDiskUsage,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("diskJson", payload),
            new DataParameter("ts", receivedAt));
    }

    private async Task UpsertHardwareHealthAsync(DatabaseContext db, long machineId, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        await db.ExecuteAsync(
            _dialect.UpsertHardwareHealth,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("hwJson", payload),
            new DataParameter("ts", receivedAt));
    }

    private async Task UpsertPackageUpdatesAsync(DatabaseContext db, long machineId, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        PackageUpdatesPayload? info = Deserialize<PackageUpdatesPayload>(payload);
        if (info is null)
        {
            return;
        }

        int pending = info.Updates.Count;
        int security = info.Updates.Count(u => u.IsSecurityUpdate);

        await db.ExecuteAsync(
            _dialect.UpsertPackageUpdates,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("pending", pending),
            new DataParameter("security", security),
            new DataParameter("ts", receivedAt));
    }

    private async Task UpsertServiceStatusAsync(DatabaseContext db, long machineId, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        ServiceStatusPayload? info = Deserialize<ServiceStatusPayload>(payload);
        if (info is null)
        {
            return;
        }

        int total = info.Services.Count;
        int failed = info.Services.Count(s =>
            string.Equals(s.ActiveState, "failed", StringComparison.OrdinalIgnoreCase));

        await db.ExecuteAsync(
            _dialect.UpsertServiceStatus,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("total", total),
            new DataParameter("failed", failed),
            new DataParameter("ts", receivedAt));
    }

    private async Task UpsertCpuInfoAsync(DatabaseContext db, long machineId, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        CpuInfoPayload? info = Deserialize<CpuInfoPayload>(payload);
        if (info is null)
        {
            return;
        }

        int physicalCpus = int.TryParse(info.NumberOfCores, out int cores) ? cores : 0;

        await db.ExecuteAsync(
            _dialect.UpsertCpuInfo,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("cpuType", info.ProcessorType),
            new DataParameter("physCpus", physicalCpus),
            new DataParameter("logCpus", info.LogicalProcessors),
            new DataParameter("ts", receivedAt));
    }

    private async Task UpsertMemoryInfoAsync(DatabaseContext db, long machineId, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        MemoryInfoPayload? info = Deserialize<MemoryInfoPayload>(payload);
        if (info is null)
        {
            return;
        }

        await db.ExecuteAsync(
            _dialect.UpsertMemoryInfo,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("swapTotal", info.SwapTotal),
            new DataParameter("swapFree", info.SwapFree),
            new DataParameter("ts", receivedAt));
    }

    private async Task UpsertDiskInfoAsync(DatabaseContext db, long machineId, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        await db.ExecuteAsync(
            _dialect.UpsertDiskInfo,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("diskJson", payload),
            new DataParameter("ts", receivedAt));
    }

    private async Task UpsertSshSessionsAsync(DatabaseContext db, long machineId, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        // For SQLite dialect, we need to read existing sessions first.
        string? existingSessions = null;
        if (_dialect is SqliteSqlDialect)
        {
            existingSessions = await db.MachineStates
                .Where(ms => ms.MachineId == machineId)
                .Select(ms => ms.SshSessions)
                .FirstOrDefaultAsync(ct);
        }

        (string sql, string sessionsValue) = _dialect.BuildUpsertSshSessions(existingSessions, payload);

        await db.ExecuteAsync(
            sql,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("sshJson", sessionsValue),
            new DataParameter("ts", receivedAt));
    }

    private async Task UpsertLastTelemetryAsync(DatabaseContext db, long machineId, DateTimeOffset receivedAt, CancellationToken ct)
    {
        await db.ExecuteAsync(
            _dialect.UpsertLastTelemetry,
            ct,
            new DataParameter("machineId", machineId),
            new DataParameter("ts", receivedAt));
    }

    internal T? Deserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonDefaults.SnakeCase);
        }
        catch (JsonException ex)
        {
            string truncated = json.Length > 200 ? json[..200] + "..." : json;
            _logger.LogWarning(ex, "Failed to deserialize {Type} payload: {Payload}", typeof(T).Name, truncated);

            return null;
        }
    }
}
