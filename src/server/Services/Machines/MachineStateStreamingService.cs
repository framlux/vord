// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Telemetry;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Continuously polls MachineTelemetry by high-water mark and applies targeted
/// per-column UPDATEs to MachineStateSummary and MachineStateDetail.
/// Only updates the columns relevant to each telemetry type plus LastSeenAt.
/// Does not compute health — that is handled by <see cref="HealthSweepService"/>.
/// Processes one row at a time for O(1) memory usage.
/// </summary>
public sealed class MachineStateStreamingService : BackgroundService
{
    /// <summary>
    /// Number of telemetry rows to fetch per poll cycle.
    /// </summary>
    internal const int BatchSize = 50;

    /// <summary>
    /// How long to sleep when no new telemetry rows are available.
    /// </summary>
    internal static readonly TimeSpan IdleSleepDuration = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often to persist the high-water mark to the database.
    /// </summary>
    private const int PersistHighWaterMarkEveryNRows = 500;

    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(30);
    private const string LockKey = "lock:state-streaming";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISqlDialect _dialect;
    private readonly IDistributedLock _distributedLock;
    private readonly IServerSettingsCache _settingsCache;
    private readonly ILogger<MachineStateStreamingService> _logger;

    private long _highWaterMark;
    private int _rowsSinceLastPersist;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineStateStreamingService"/> class.
    /// </summary>
    public MachineStateStreamingService(
        IServiceScopeFactory scopeFactory,
        ISqlDialect dialect,
        IDistributedLock distributedLock,
        IServerSettingsCache settingsCache,
        ILogger<MachineStateStreamingService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _distributedLock = distributedLock ?? throw new ArgumentNullException(nameof(distributedLock));
        _settingsCache = settingsCache ?? throw new ArgumentNullException(nameof(settingsCache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        _logger.LogInformation("Machine state streaming service started");

        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await using LockHandle? lockHandle = await _distributedLock.TryAcquireAsync(LockKey, LockTtl);
                if (lockHandle is null)
                {
                    _logger.LogDebug("State streaming: another instance holds the lock, waiting");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

                    continue;
                }

                await LoadHighWaterMarkAsync(stoppingToken);
                await StreamLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in state streaming service, will retry");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("Machine state streaming service stopped");
    }

    /// <summary>
    /// Main streaming loop: continuously polls MachineTelemetry for new rows
    /// and applies targeted UPDATEs to the summary and detail tables.
    /// </summary>
    private async Task StreamLoopAsync(CancellationToken ct)
    {
        while (ct.IsCancellationRequested == false)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IMachineStateRepository repo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

            DateTimeOffset streamingWindow = DateTimeOffset.UtcNow.AddDays(-2);
            List<MachineTelemetry> batch = await repo.GetTelemetryBatchAsync(
                _highWaterMark, streamingWindow, BatchSize, ct);

            if (batch.Count == 0)
            {
                await Task.Delay(IdleSleepDuration, ct);

                continue;
            }

            foreach (MachineTelemetry row in batch)
            {
                try
                {
                    await ProcessTelemetryRowAsync(repo, row, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process telemetry row {Id} for machine {MachineId}", row.Id, row.MachineId);
                }

                _highWaterMark = row.Id;
                _rowsSinceLastPersist++;

                if (_rowsSinceLastPersist >= PersistHighWaterMarkEveryNRows)
                {
                    await PersistHighWaterMarkAsync(ct);
                    _rowsSinceLastPersist = 0;
                }
            }

            // Persist after each batch to avoid re-processing on crash.
            await PersistHighWaterMarkAsync(ct);
            _rowsSinceLastPersist = 0;
        }
    }

    /// <summary>
    /// Processes a single telemetry row by applying a targeted UPDATE to the
    /// summary and/or detail tables based on the telemetry type.
    /// </summary>
    internal async Task ProcessTelemetryRowAsync(IMachineStateRepository repo, MachineTelemetry row, CancellationToken ct)
    {
        DateTimeOffset receivedAt = row.ReceivedAt;
        long machineId = row.MachineId;

        switch (row.TelemetryType)
        {
            case TelemetryTypeIds.SystemInfo:
                await ProcessSystemInfoAsync(repo, machineId, row.Payload, receivedAt, ct);
                break;

            case TelemetryTypeIds.OsVersion:
                await ProcessOsVersionAsync(repo, machineId, row.Payload, receivedAt, ct);
                break;

            case TelemetryTypeIds.CpuInfo:
                await ProcessCpuInfoAsync(repo, machineId, row.Payload, receivedAt, ct);
                break;

            case TelemetryTypeIds.MemoryInfo:
                await ProcessMemoryInfoAsync(repo, machineId, row.Payload, receivedAt, ct);
                break;

            case TelemetryTypeIds.DiskInfo:
                await ProcessDiskInfoAsync(repo, machineId, row.Payload, receivedAt, ct);
                break;

            case TelemetryTypeIds.CpuUsage:
                await ProcessCpuUsageAsync(repo, machineId, row.Payload, receivedAt, ct);
                break;

            case TelemetryTypeIds.MemoryUsage:
                await ProcessMemoryUsageAsync(repo, machineId, row.Payload, receivedAt, ct);
                break;

            case TelemetryTypeIds.DiskUsage:
                await ProcessDiskUsageAsync(repo, machineId, row.Payload, receivedAt, ct);
                break;

            case TelemetryTypeIds.SshSessions:
                await ProcessSshSessionsAsync(repo, machineId, row.Payload, receivedAt, ct);
                break;

            case TelemetryTypeIds.HardwareHealth:
                await ProcessHardwareHealthAsync(repo, machineId, row.Payload, receivedAt, ct);
                break;

            case TelemetryTypeIds.PackageUpdates:
                await ProcessPackageUpdatesAsync(repo, machineId, row.Payload, receivedAt, ct);
                break;

            case TelemetryTypeIds.ServiceStatus:
                await ProcessServiceStatusAsync(repo, machineId, row.Payload, receivedAt, ct);
                break;

            default:
                _logger.LogDebug("Unknown telemetry type {Type} for machine {MachineId}", row.TelemetryType, machineId);
                break;
        }
    }

    private static async Task ProcessSystemInfoAsync(IMachineStateRepository repo, long machineId, string payload, DateTimeOffset ts, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        string? hostname = root.TryGetProperty("hostname", out JsonElement h) ? h.GetString() : null;
        string? hardwareModel = root.TryGetProperty("hardware_model", out JsonElement hm) ? hm.GetString() : null;
        string? hardwareVendor = root.TryGetProperty("hardware_vendor", out JsonElement hv) ? hv.GetString() : null;
        string? hardwareSerial = root.TryGetProperty("hardware_serial", out JsonElement hs) ? hs.GetString() : null;
        string? cpuBrand = root.TryGetProperty("cpu_brand", out JsonElement cb) ? cb.GetString() : null;
        int? cpuCores = root.TryGetProperty("cpu_cores", out JsonElement cc) ? cc.GetInt32() : null;
        long? memoryTotal = root.TryGetProperty("memory_total_bytes", out JsonElement mt) ? mt.GetInt64() : null;
        long? uptime = root.TryGetProperty("uptime_seconds", out JsonElement ut) ? ut.GetInt64() : null;
        string? biosVersion = root.TryGetProperty("bios_version", out JsonElement bv) ? bv.GetString() : null;
        string? ipAddresses = root.TryGetProperty("ip_addresses", out JsonElement ip) ? ip.GetRawText() : null;

        await repo.UpdateSystemInfoSummaryAsync(machineId, hostname, hardwareModel, ipAddresses, ts, ct);
        await repo.UpdateSystemInfoDetailAsync(machineId, hardwareVendor, hardwareSerial, cpuBrand, cpuCores, memoryTotal, uptime, biosVersion, ct);
    }

    private static async Task ProcessOsVersionAsync(IMachineStateRepository repo, long machineId, string payload, DateTimeOffset ts, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        string? osName = root.TryGetProperty("os_name", out JsonElement on) ? on.GetString() : null;
        string? osVersion = root.TryGetProperty("os_version", out JsonElement ov) ? ov.GetString() : null;
        string? kernel = root.TryGetProperty("kernel", out JsonElement k) ? k.GetString() : null;

        await repo.UpdateOsVersionSummaryAsync(machineId, osName, osVersion, ts, ct);
        await repo.UpdateOsVersionDetailAsync(machineId, kernel, ct);
    }

    private static async Task ProcessCpuInfoAsync(IMachineStateRepository repo, long machineId, string payload, DateTimeOffset ts, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        string? cpuType = root.TryGetProperty("cpu_type", out JsonElement ct2) ? ct2.GetString() : null;
        int? physCpus = root.TryGetProperty("physical_cpus", out JsonElement pc) ? pc.GetInt32() : null;
        int? logCpus = root.TryGetProperty("logical_cpus", out JsonElement lc) ? lc.GetInt32() : null;

        await repo.UpdateCpuInfoSummaryAsync(machineId, ts, ct);
        await repo.UpdateCpuInfoDetailAsync(machineId, cpuType, physCpus, logCpus, ct);
    }

    private static async Task ProcessMemoryInfoAsync(IMachineStateRepository repo, long machineId, string payload, DateTimeOffset ts, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        long? swapTotal = root.TryGetProperty("swap_total_bytes", out JsonElement st) ? st.GetInt64() : null;
        long? swapFree = root.TryGetProperty("swap_free_bytes", out JsonElement sf) ? sf.GetInt64() : null;

        await repo.UpdateMemoryInfoSummaryAsync(machineId, ts, ct);
        await repo.UpdateMemoryInfoDetailAsync(machineId, swapTotal, swapFree, ct);
    }

    private static async Task ProcessDiskInfoAsync(IMachineStateRepository repo, long machineId, string payload, DateTimeOffset ts, CancellationToken ct)
    {
        await repo.UpdateDiskInfoSummaryAsync(machineId, ts, ct);
        await repo.UpdateDiskInfoDetailAsync(machineId, payload, ct);
    }

    private static async Task ProcessCpuUsageAsync(IMachineStateRepository repo, long machineId, string payload, DateTimeOffset ts, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        int? cpuPercent = root.TryGetProperty("cpu_usage_percent", out JsonElement cp) ? cp.GetInt32() : null;

        await repo.UpdateCpuUsageSummaryAsync(machineId, cpuPercent, ts, ct);
    }

    private static async Task ProcessMemoryUsageAsync(IMachineStateRepository repo, long machineId, string payload, DateTimeOffset ts, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        long? memUsed = root.TryGetProperty("memory_used", out JsonElement mu) ? mu.GetInt64() : null;
        int? memPercent = root.TryGetProperty("memory_usage_percent", out JsonElement mp) ? mp.GetInt32() : null;

        await repo.UpdateMemoryUsageSummaryAsync(machineId, memPercent, ts, ct);
        await repo.UpdateMemoryUsageDetailAsync(machineId, memUsed, ct);
    }

    private static async Task ProcessDiskUsageAsync(IMachineStateRepository repo, long machineId, string payload, DateTimeOffset ts, CancellationToken ct)
    {
        int maxDiskUsage = ComputeMaxDiskUsagePercent(payload);

        await repo.UpdateDiskUsageSummaryAsync(machineId, maxDiskUsage, ts, ct);
        await repo.UpdateDiskUsageDetailAsync(machineId, payload, ct);
    }

    private static async Task ProcessSshSessionsAsync(IMachineStateRepository repo, long machineId, string payload, DateTimeOffset ts, CancellationToken ct)
    {
        await repo.UpdateSshSessionsSummaryAsync(machineId, ts, ct);
        await repo.UpdateSshSessionsDetailAsync(machineId, payload, ct);
    }

    private static async Task ProcessHardwareHealthAsync(IMachineStateRepository repo, long machineId, string payload, DateTimeOffset ts, CancellationToken ct)
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = ComputeHardwareHealthFlags(payload);

        await repo.UpdateHardwareHealthSummaryAsync(machineId, hasDiskIssue, hasHardwareIssue, ts, ct);
        await repo.UpdateHardwareHealthDetailAsync(machineId, payload, ct);
    }

    private static async Task ProcessPackageUpdatesAsync(IMachineStateRepository repo, long machineId, string payload, DateTimeOffset ts, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        int? pending = root.TryGetProperty("pending_updates", out JsonElement pu) ? pu.GetInt32() : null;
        int? security = root.TryGetProperty("security_updates", out JsonElement su) ? su.GetInt32() : null;

        await repo.UpdatePackageUpdatesSummaryAsync(machineId, pending, security, ts, ct);
    }

    private static async Task ProcessServiceStatusAsync(IMachineStateRepository repo, long machineId, string payload, DateTimeOffset ts, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        int? total = root.TryGetProperty("total_services", out JsonElement ts2) ? ts2.GetInt32() : null;
        int? failed = root.TryGetProperty("failed_services", out JsonElement fs) ? fs.GetInt32() : null;

        await repo.UpdateServiceStatusSummaryAsync(machineId, total, failed, ts, ct);
    }

    /// <summary>
    /// Computes the maximum disk usage percentage across all disks in the JSONB payload.
    /// </summary>
    internal static int ComputeMaxDiskUsagePercent(string diskUsagesJson)
    {
        int maxUsage = 0;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(diskUsagesJson);
            JsonElement root = doc.RootElement;

            // The payload is serialized from DiskUtilizationRecord which wraps disks in a "disks" property.
            JsonElement disksElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                disksElement = root;
            }
            else if (root.TryGetProperty("disks", out JsonElement d) && (d.ValueKind == JsonValueKind.Array))
            {
                disksElement = d;
            }
            else
            {
                return maxUsage;
            }

            foreach (JsonElement disk in disksElement.EnumerateArray())
            {
                if (disk.TryGetProperty("usage_percent", out JsonElement up))
                {
                    int usage = up.GetInt32();
                    if (usage > maxUsage)
                    {
                        maxUsage = usage;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed payload — return 0.
        }

        return maxUsage;
    }

    /// <summary>
    /// Computes hardware health flags from the JSONB payload.
    /// Returns (hasDiskHealthIssue, hasHardwareIssue).
    /// </summary>
    internal static (bool HasDiskHealthIssue, bool HasHardwareIssue) ComputeHardwareHealthFlags(string hardwareHealthJson)
    {
        bool hasDiskIssue = false;
        bool hasHardwareIssue = false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(hardwareHealthJson);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("disk_smart", out JsonElement diskSmart) &&
                diskSmart.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement disk in diskSmart.EnumerateArray())
                {
                    if (disk.TryGetProperty("health_status", out JsonElement status) &&
                        string.Equals(status.GetString(), "FAILED", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDiskIssue = true;

                        break;
                    }
                }
            }

            if (root.TryGetProperty("fans", out JsonElement fans) &&
                fans.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement fan in fans.EnumerateArray())
                {
                    if (fan.TryGetProperty("rpm", out JsonElement rpm) && (rpm.GetInt32() == 0))
                    {
                        hasHardwareIssue = true;

                        break;
                    }
                }
            }

            if ((hasHardwareIssue == false) &&
                root.TryGetProperty("power_supplies", out JsonElement powerSupplies) &&
                powerSupplies.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement ps in powerSupplies.EnumerateArray())
                {
                    if (ps.TryGetProperty("status", out JsonElement psStatus) &&
                        (string.Equals(psStatus.GetString(), "OK", StringComparison.OrdinalIgnoreCase) == false))
                    {
                        hasHardwareIssue = true;

                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed payload — leave flags as false.
        }

        return (hasDiskIssue, hasHardwareIssue);
    }

    private async Task LoadHighWaterMarkAsync(CancellationToken ct)
    {
        string? stored = await _settingsCache.GetSettingAsync(
            ServerConfigurationSettingKeys.StreamingHighWaterMark, ct);

        if (stored is not null && long.TryParse(stored, out long hwm))
        {
            _highWaterMark = hwm;
        }
        else
        {
            _highWaterMark = 0;
        }

        _logger.LogInformation("State streaming starting from high-water mark {HighWaterMark}", _highWaterMark);
    }

    private async Task PersistHighWaterMarkAsync(CancellationToken ct)
    {
        await _settingsCache.SetSettingAsync(
            ServerConfigurationSettingKeys.StreamingHighWaterMark,
            _highWaterMark.ToString(),
            ct);
    }
}
