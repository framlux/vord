// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Globalization;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines.Projection;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Continuously polls MachineTelemetry by high-water mark and projects new rows into
/// MachineStateSummary and MachineStateDetail. Each batch is collapsed to one
/// <see cref="MachineStatePatch"/> per machine via <see cref="MachineStateBatchCollapser"/>,
/// so the service issues at most one UPDATE per table per machine rather than one per row.
/// For each telemetry type the latest row by (ReceivedAt, Id) wins, so a backfilled row that
/// arrives with a higher Id but an older ReceivedAt can never overwrite a fresher reading.
/// LastSeenAt is set to MAX(ReceivedAt) across the machine's batch rows and is monotonic —
/// the apply never moves an already-stored LastSeenAt backward.
/// Does not compute health — that is handled by HealthSweepCoordinatorJob + HealthSweepTenantJob.
/// The raw MachineTelemetry table is never modified, so history/detail read paths are untouched.
/// </summary>
public sealed class MachineStateStreamingService : BackgroundService
{
    /// <summary>
    /// Number of telemetry rows to fetch per poll cycle.
    /// </summary>
    internal const int BatchSize = 200;

    /// <summary>
    /// How long to sleep when no new telemetry rows are available.
    /// </summary>
    internal static readonly TimeSpan IdleSleepDuration = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Default delay before the first batch poll on service startup. Production value is 5 s
    /// to let dependencies warm up; tests pass a shorter override via the constructor so the
    /// suite remains fast and deterministic.
    /// </summary>
    internal static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum number of telemetry rows to process concurrently within a batch.
    /// </summary>
    private const int MaxDegreeOfParallelism = 4;

    private const string LockKey = LockNames.StateStreaming;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISqlDialect _dialect;
    private readonly IAdvisoryLockProvider _advisoryLockProvider;
    private readonly IServerSettingsCache _settingsCache;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _startupDelay;
    private readonly ILogger<MachineStateStreamingService> _logger;

    private long _highWaterMark;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineStateStreamingService"/> class.
    /// </summary>
    /// <param name="scopeFactory">Service scope factory for resolving scoped repositories per batch.</param>
    /// <param name="dialect">SQL dialect used by downstream repository calls.</param>
    /// <param name="advisoryLockProvider">Provides exclusive coordination across replicas.</param>
    /// <param name="settingsCache">Stores the streaming high-water mark across restarts.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">Clock abstraction used for loop delays so tests do not depend on wall-clock time.</param>
    /// <param name="startupDelay">Optional override for the startup delay; tests use a short value to keep the suite fast.</param>
    public MachineStateStreamingService(
        IServiceScopeFactory scopeFactory,
        ISqlDialect dialect,
        IAdvisoryLockProvider advisoryLockProvider,
        IServerSettingsCache settingsCache,
        ILogger<MachineStateStreamingService> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? startupDelay = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(advisoryLockProvider);
        ArgumentNullException.ThrowIfNull(settingsCache);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _dialect = dialect;
        _advisoryLockProvider = advisoryLockProvider;
        _settingsCache = settingsCache;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startupDelay = startupDelay ?? DefaultStartupDelay;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_startupDelay > TimeSpan.Zero)
        {
            await Task.Delay(_startupDelay, _timeProvider, stoppingToken);
        }

        _logger.LogInformation("Machine state streaming service started");

        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await using IAsyncDisposable? lockHandle = await _advisoryLockProvider.TryAcquireAsync(LockKey, stoppingToken);
                if (lockHandle is null)
                {
                    _logger.LogDebug("State streaming: another instance holds the lock, waiting");
                    await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, stoppingToken);

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
                await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("Machine state streaming service stopped");
    }

    /// <summary>
    /// Main streaming loop: continuously polls MachineTelemetry for new rows, collapses each
    /// batch to one <see cref="MachineStatePatch"/> per machine, and applies at most one UPDATE
    /// per table per machine. Machines are applied concurrently for throughput.
    /// </summary>
    private async Task StreamLoopAsync(CancellationToken ct)
    {
        while (ct.IsCancellationRequested == false)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IMachineStateRepository repo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

            DateTimeOffset streamingWindow = _timeProvider.GetUtcNow().AddDays(-2);
            List<MachineTelemetry> batch = await repo.GetTelemetryBatchAsync(
                _highWaterMark, streamingWindow, BatchSize, ct);

            if (batch.Count == 0)
            {
                await Task.Delay(IdleSleepDuration, _timeProvider, ct);

                continue;
            }

            // Collapse the batch into one patch per machine, then apply at most one UPDATE per
            // table per machine. For each telemetry type the latest row by (ReceivedAt, Id) wins.
            CollapseResult collapse = MachineStateBatchCollapser.Collapse(batch);

            foreach (SkippedTelemetryRow skip in collapse.Skipped)
            {
                _logger.LogWarning(
                    "Skipped malformed telemetry row {RowId} (type {TelemetryType}) for machine {MachineId}",
                    skip.RowId, skip.TelemetryType, skip.MachineId);
            }

            await Parallel.ForEachAsync(collapse.Patches, new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                CancellationToken = ct
            }, async (patch, token) =>
            {
                using IServiceScope innerScope = _scopeFactory.CreateScope();
                IMachineStateRepository innerRepo = innerScope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

                await innerRepo.ApplySummaryPatchAsync(MapSummary(patch), token);

                if (patch.HasDetailChanges == true)
                {
                    await innerRepo.ApplyDetailPatchAsync(MapDetail(patch), token);
                }
            });

            // Advance the high-water mark and persist after every batch — re-processing rows on
            // crash is more expensive than the extra DB write per batch.
            _highWaterMark = batch[^1].Id;
            await PersistHighWaterMarkAsync(ct);
        }
    }

    /// <summary>
    /// Maps a services.core projection patch onto the database-layer summary carrier. Each owning
    /// type's presence flag is set from whether its fragment is present, and the fragment's columns
    /// are copied across. Keeps the dependency direction (services.core to database) intact.
    /// </summary>
    private static MachineSummaryPatch MapSummary(MachineStatePatch patch)
    {
        return new MachineSummaryPatch
        {
            MachineId = patch.MachineId,
            LastSeenAt = patch.LastSeenAt,
            HasSystemInfo = patch.SystemInfo is not null,
            Hostname = patch.SystemInfo?.Hostname,
            HardwareModel = patch.SystemInfo?.HardwareModel,
            IpAddresses = patch.SystemInfo?.IpAddresses,
            HasOsVersion = patch.OsVersion is not null,
            OsName = patch.OsVersion?.OsName,
            OsVersion = patch.OsVersion?.OsVersion,
            HasCpuUsage = patch.CpuUsage is not null,
            CpuUsagePercent = patch.CpuUsage?.CpuUsagePercent,
            HasMemoryUsage = patch.MemoryUsage is not null,
            MemoryUsagePercent = patch.MemoryUsage?.MemoryUsagePercent,
            HasDiskUsage = patch.DiskUsage is not null,
            MaxDiskUsagePercent = patch.DiskUsage?.MaxDiskUsagePercent,
            HasHardwareHealth = patch.HardwareHealth is not null,
            HasDiskHealthIssue = patch.HardwareHealth?.HasDiskHealthIssue,
            HasHardwareIssue = patch.HardwareHealth?.HasHardwareIssue,
            HasPackageUpdates = patch.PackageUpdates is not null,
            PendingUpdates = patch.PackageUpdates?.PendingUpdates,
            SecurityUpdates = patch.PackageUpdates?.SecurityUpdates,
            HasServiceStatus = patch.ServiceStatus is not null,
            TotalServices = patch.ServiceStatus?.TotalServices,
            FailedServices = patch.ServiceStatus?.FailedServices,
        };
    }

    /// <summary>
    /// Maps a services.core projection patch onto the database-layer detail carrier. Each owning
    /// type's presence flag is set from whether its fragment is present, and the fragment's columns
    /// are copied across. Keeps the dependency direction (services.core to database) intact.
    /// </summary>
    private static MachineDetailPatch MapDetail(MachineStatePatch patch)
    {
        return new MachineDetailPatch
        {
            MachineId = patch.MachineId,
            HasSystemInfo = patch.SystemInfo is not null,
            HardwareVendor = patch.SystemInfo?.HardwareVendor,
            HardwareSerial = patch.SystemInfo?.HardwareSerial,
            CpuBrand = patch.SystemInfo?.CpuBrand,
            CpuCores = patch.SystemInfo?.CpuCores,
            MemoryTotalBytes = patch.SystemInfo?.MemoryTotalBytes,
            UptimeSeconds = patch.SystemInfo?.UptimeSeconds,
            BiosVersion = patch.SystemInfo?.BiosVersion,
            HasOsVersion = patch.OsVersion is not null,
            Kernel = patch.OsVersion?.Kernel,
            HasCpuInfo = patch.CpuInfo is not null,
            CpuType = patch.CpuInfo?.CpuType,
            CpuPhysicalCpus = patch.CpuInfo?.CpuPhysicalCpus,
            CpuLogicalCpus = patch.CpuInfo?.CpuLogicalCpus,
            HasMemoryInfo = patch.MemoryInfo is not null,
            SwapTotalBytes = patch.MemoryInfo?.SwapTotalBytes,
            SwapFreeBytes = patch.MemoryInfo?.SwapFreeBytes,
            HasMemoryUsage = patch.MemoryUsage is not null,
            MemoryUsedBytes = patch.MemoryUsage?.MemoryUsedBytes,
            HasDiskInfo = patch.DiskInfo is not null,
            DiskInfos = patch.DiskInfo?.DiskInfos,
            HasDiskUsage = patch.DiskUsage is not null,
            DiskUsages = patch.DiskUsage?.DiskUsages,
            HasSshSessions = patch.SshSessions is not null,
            SshSessions = patch.SshSessions?.SshSessions,
            HasHardwareHealth = patch.HardwareHealth is not null,
            HardwareHealth = patch.HardwareHealth?.HardwareHealth,
        };
    }

    private async Task LoadHighWaterMarkAsync(CancellationToken ct)
    {
        string? stored = await _settingsCache.GetSettingAsync(
            ServerConfigurationSettingKeys.StreamingHighWaterMark, ct);

        // Parse with invariant culture and explicit NumberStyles so the round-trip is stable on
        // non-en hosts (the same applies to the symmetric Persist path below).
        if (stored is not null && long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out long hwm))
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
            _highWaterMark.ToString(CultureInfo.InvariantCulture),
            ct);
    }
}
