// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines.Projection;
using Framlux.FleetManagement.Services.Core.Options;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Continuously polls MachineTelemetry by high-water mark and projects new rows into
/// MachineStateSummary and MachineStateDetail. Each batch is collapsed to one
/// <see cref="MachineStatePatch"/> per machine via <see cref="MachineStateBatchCollapser"/>,
/// so the service issues at most one UPDATE per table per machine rather than one per row.
/// For each telemetry type the latest row by (ServerReceivedAt, Id) wins, so a backfilled row that
/// arrives with a higher Id but an older server receipt can never overwrite a fresher reading.
/// LastSeenAt is set to MAX(ServerReceivedAt) across the machine's batch rows and is monotonic —
/// the apply never moves an already-stored LastSeenAt backward. ServerReceivedAt is the server-stamped
/// receipt time, so recency is immune to agent clock skew.
/// Does not compute health — that is handled by HealthSweepCoordinatorJob + HealthSweepTenantJob.
/// The raw MachineTelemetry table is never modified, so history/detail read paths are untouched.
/// </summary>
public sealed class MachineStateStreamingService : BackgroundService
{
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

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISqlDialect _dialect;
    private readonly IAdvisoryLockProvider _advisoryLockProvider;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _startupDelay;
    private readonly ILogger<MachineStateStreamingService> _logger;
    private readonly int _shardIndex;
    private readonly int _shardCount;
    private readonly int _batchSize;
    private readonly string _lockKey;

    private long _highWaterMark;
    private bool _highWaterMarkLoaded;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineStateStreamingService"/> class.
    /// </summary>
    /// <param name="scopeFactory">Service scope factory for resolving scoped repositories per batch.</param>
    /// <param name="dialect">SQL dialect used by downstream repository calls.</param>
    /// <param name="advisoryLockProvider">Provides exclusive coordination across replicas.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="shardIndex">The projection shard this instance owns under modulo partitioning.</param>
    /// <param name="streamingOptions">Streaming options carrying the shard count and batch size.</param>
    /// <param name="timeProvider">Clock abstraction used for loop delays so tests do not depend on wall-clock time.</param>
    /// <param name="startupDelay">Optional override for the startup delay; tests use a short value to keep the suite fast.</param>
    public MachineStateStreamingService(
        IServiceScopeFactory scopeFactory,
        ISqlDialect dialect,
        IAdvisoryLockProvider advisoryLockProvider,
        ILogger<MachineStateStreamingService> logger,
        int shardIndex,
        IOptions<StreamingOptions> streamingOptions,
        TimeProvider? timeProvider = null,
        TimeSpan? startupDelay = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(advisoryLockProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(streamingOptions);

        StreamingOptions options = streamingOptions.Value;

        _scopeFactory = scopeFactory;
        _dialect = dialect;
        _advisoryLockProvider = advisoryLockProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startupDelay = startupDelay ?? DefaultStartupDelay;
        _logger = logger;
        _shardIndex = shardIndex;
        _shardCount = options.ShardCount;
        _batchSize = options.BatchSize;
        _lockKey = StreamingShardCalculator.LockNameForShard(_shardIndex);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_startupDelay > TimeSpan.Zero)
        {
            await Task.Delay(_startupDelay, _timeProvider, stoppingToken);
        }

        _logger.LogInformation(
            "Machine state streaming service started for shard {ShardIndex} of {ShardCount}", _shardIndex, _shardCount);

        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await using IAdvisoryLock? lockHandle = await _advisoryLockProvider.TryAcquireAsync(_lockKey, stoppingToken);
                if (lockHandle is null)
                {
                    _logger.LogDebug("State streaming: another instance holds the lock, waiting");
                    await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, stoppingToken);

                    continue;
                }

                bool lockLost = false;
                try
                {
                    await LoadHighWaterMarkAsync(stoppingToken);
                    lockLost = await StreamLoopAsync(lockHandle, stoppingToken);
                }
                finally
                {
                    if (lockLost == false)
                    {
                        // Flush the final high-water mark while the per-shard advisory lock is verified
                        // STILL held, with a non-cancellable token. This ensures a cancelled stopping token
                        // cannot skip the final write, and the write completes before the lock is released —
                        // so a successor that takes over the shard is not clobbered by this cursor. If the
                        // lock was lost mid-stream a successor may already own the shard and have advanced
                        // the cursor further, so we deliberately do NOT flush.
                        await FlushHighWaterMarkAsync();
                    }
                    else
                    {
                        _logger.LogWarning(
                            "State streaming: shard {ShardIndex} lock lost; abandoning the shard without a final cursor flush to avoid clobbering the successor",
                            _shardIndex);
                    }
                }
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
    /// Persists the current in-memory high-water mark while the per-shard lock is held, using a
    /// non-cancellable token. Only writes when a mark has actually been loaded, and never throws —
    /// a failed final persist costs at most an idempotent re-projection of one batch.
    /// </summary>
    private async Task FlushHighWaterMarkAsync()
    {
        if (_highWaterMarkLoaded == false)
        {
            return;
        }

        try
        {
            await PersistHighWaterMarkAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist final high-water mark for shard {ShardIndex}", _shardIndex);
        }
    }

    /// <summary>
    /// Main streaming loop: continuously polls MachineTelemetry for new rows, collapses each
    /// batch to one <see cref="MachineStatePatch"/> per machine, and applies at most one UPDATE
    /// per table per machine. Machines are applied concurrently for throughput.
    /// </summary>
    private async Task<bool> StreamLoopAsync(IAdvisoryLock lockHandle, CancellationToken ct)
    {
        while (ct.IsCancellationRequested == false)
        {
            // Verify the shard lock's session is still alive before doing any work this iteration. A
            // silently-dropped lock session (failover, partition, idle-in-transaction timeout) would
            // otherwise let this process keep projecting while a successor also owns the shard; returning
            // true abandons the shard without a final flush so we cannot clobber the successor's cursor.
            if (await lockHandle.IsAliveAsync(ct) == false)
            {
                return true;
            }

            using IServiceScope scope = _scopeFactory.CreateScope();
            IMachineStateRepository repo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

            DateTimeOffset streamingWindow = _timeProvider.GetUtcNow().AddDays(-2);
            List<MachineTelemetry> batch = await repo.GetTelemetryBatchAsync(
                _highWaterMark, streamingWindow, _batchSize, _shardIndex, _shardCount, ct);

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

        // Normal exit (stopping token cancelled) — the lock is still held, so the caller flushes.
        return false;
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
        using IServiceScope scope = _scopeFactory.CreateScope();
        IMachineStateRepository repo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

        _highWaterMark = await repo.GetProjectionCursorAsync(_shardIndex, ct) ?? 0;
        _highWaterMarkLoaded = true;

        _logger.LogInformation("State streaming starting from high-water mark {HighWaterMark}", _highWaterMark);
    }

    private async Task PersistHighWaterMarkAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IMachineStateRepository repo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

        await repo.SetProjectionCursorAsync(_shardIndex, _highWaterMark, ct);
    }
}
