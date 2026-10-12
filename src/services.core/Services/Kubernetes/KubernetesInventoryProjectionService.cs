// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Telemetry;
using System.Text.Json;

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Continuously reads Kubernetes node reports (telemetry type 14) past its own cursor and applies
/// each one to the Kubernetes inventory, so a tenant's clusters and nodes follow what its machines
/// report about themselves.
/// </summary>
/// <remarks>
/// <para>
/// The service is separate from the machine-state projection on purpose: a report that cannot be
/// applied must never hold back, or take down, the projection of CPU and memory readings. It holds a
/// single global lock so one replica applies the stream at a time, and every failure is contained in
/// the poll that met it. An exception that escaped <see cref="ExecuteAsync"/> would stop the host and
/// with it the state-streaming services, so the loop only ends when the service is stopped.
/// </para>
/// <para>
/// The cursor moves in the same transaction as each applied row, and a skipped row moves it with a
/// standalone write, so nothing is held in memory and there is nothing to flush on shutdown. A row is
/// skipped, counted and logged when it can never be applied: an unreadable payload, a failure that
/// retrying cannot fix, or a temporary failure that outlasted its retries.
/// </para>
/// </remarks>
public sealed class KubernetesInventoryProjectionService : BackgroundService
{
    /// <summary>The number of telemetry rows read per poll.</summary>
    internal const int BatchSize = 200;

    /// <summary>
    /// How many times a row is tried when each attempt fails with a temporary error. The attempts are
    /// one <see cref="PollInterval"/> apart, so the budget spans longer than a connection blip or a
    /// database failover.
    /// </summary>
    internal const int MaxTransientAttempts = 5;

    /// <summary>How long to wait after an empty batch, a temporary failure or a crashed poll.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How far back the projection still reads. A report older than this is outside the window by
    /// design, and a machine that is still running has sent a newer one.
    /// </summary>
    internal static readonly TimeSpan ReadWindow = TimeSpan.FromDays(8);

    /// <summary>
    /// How often expired candidates and stale nodes are collected, clusters with reporting disabled are
    /// reverted, and empty clusters are removed.
    /// </summary>
    internal static readonly TimeSpan GcInterval = TimeSpan.FromHours(1);

    /// <summary>How long to wait before trying again for the lock another replica holds.</summary>
    internal static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Default delay before the first poll on service startup. Tests pass a shorter override through
    /// the constructor.
    /// </summary>
    internal static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAdvisoryLockProvider _advisoryLockProvider;
    private readonly ILogger<KubernetesInventoryProjectionService> _logger;
    private readonly KubernetesInventoryMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _visibilityLag;

    private DateTimeOffset? _lastGarbageCollection;
    private long? _retryTelemetryId;
    private int _retryAttempts;

    /// <summary>
    /// Creates a new instance of the <see cref="KubernetesInventoryProjectionService"/> class.
    /// </summary>
    /// <param name="scopeFactory">Service scope factory for resolving the scoped repository per call.</param>
    /// <param name="advisoryLockProvider">Provides the single-replica lock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="streamingOptions">Streaming options carrying the visibility lag the read applies.</param>
    /// <param name="metrics">Instruments counting the rows the projection skips.</param>
    /// <param name="timeProvider">Clock abstraction used for loop delays so tests do not depend on wall-clock time.</param>
    /// <param name="startupDelay">Optional override for the startup delay; tests use a short value.</param>
    public KubernetesInventoryProjectionService(
        IServiceScopeFactory scopeFactory,
        IAdvisoryLockProvider advisoryLockProvider,
        ILogger<KubernetesInventoryProjectionService> logger,
        IOptions<StreamingOptions> streamingOptions,
        KubernetesInventoryMetrics metrics,
        TimeProvider? timeProvider = null,
        TimeSpan? startupDelay = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(advisoryLockProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(streamingOptions);
        ArgumentNullException.ThrowIfNull(metrics);

        _scopeFactory = scopeFactory;
        _advisoryLockProvider = advisoryLockProvider;
        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startupDelay = startupDelay ?? DefaultStartupDelay;
        _visibilityLag = TimeSpan.FromSeconds(Math.Max(0, streamingOptions.Value.VisibilityLagSeconds));
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_startupDelay > TimeSpan.Zero)
            {
                await Task.Delay(_startupDelay, _timeProvider, stoppingToken);
            }

            _logger.LogInformation("Kubernetes inventory projection service started");

            while (stoppingToken.IsCancellationRequested == false)
            {
                try
                {
                    await using IAdvisoryLock? lockHandle = await _advisoryLockProvider.TryAcquireAsync(
                        LockNames.KubernetesInventoryProjection, stoppingToken);
                    if (lockHandle is null)
                    {
                        _logger.LogDebug("Kubernetes inventory projection: another instance holds the lock, waiting");
                        await Task.Delay(LockRetryDelay, _timeProvider, stoppingToken);

                        continue;
                    }

                    await ProjectWhileHeldAsync(lockHandle, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Kubernetes inventory projection failed, will retry after {Delay}", PollInterval);
                    await Task.Delay(PollInterval, _timeProvider, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping is the only way out of the loop, so a cancelled delay is a normal shutdown.
        }

        _logger.LogInformation("Kubernetes inventory projection service stopped");
    }

    /// <summary>
    /// Validates and normalises a stored node report so that no value can exceed the column it will be
    /// written to. A report must name a distro: an empty one is what a payload the server failed to
    /// serialise looks like, not something an agent sends. The node name must be a valid Kubernetes
    /// node name, except that a host without a kubelet may report an empty one, because a control-plane
    /// host with no kubelet and an unusable hostname has no name to give and the repository records it
    /// as not being a node.
    /// </summary>
    /// <param name="payload">The deserialized payload.</param>
    /// <param name="row">The telemetry row the payload came from.</param>
    /// <returns>The report to apply, or null when the payload names no distro or has an unusable node name.</returns>
    internal static LocalNodeReport? NormalisePayload(KubernetesNodePayload payload, MachineTelemetry row)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(row);

        KubernetesNodePayload? normalised = KubernetesNodePayloadNormaliser.Normalise(payload);
        if (normalised == null)
        {
            return null;
        }

        return new LocalNodeReport(
            row.Id,
            row.TenantId,
            row.MachineId,
            row.ServerReceivedAt,
            normalised.Distro,
            normalised.NodeName,
            normalised.HasKubelet,
            normalised.ControlPlane,
            normalised.CaFingerprint.Length == 0 ? null : normalised.CaFingerprint,
            normalised.KubeletVersion.Length == 0 ? null : normalised.KubeletVersion,
            normalised.NodeNameSource,
            normalised.ReporterEnrolled,
            normalised.ReporterError);
    }

    /// <summary>
    /// Polls for as long as the lock is held and alive. A poll that fails is logged and followed by a
    /// delay, so no failure inside it can end the service.
    /// </summary>
    private async Task ProjectWhileHeldAsync(IAdvisoryLock lockHandle, CancellationToken ct)
    {
        _lastGarbageCollection = null;
        _retryTelemetryId = null;
        _retryAttempts = 0;

        while (ct.IsCancellationRequested == false)
        {
            // A lock session that dropped silently (failover, partition, idle timeout) would otherwise
            // let this replica keep projecting beside its successor.
            if (await lockHandle.IsAliveAsync(ct) == false)
            {
                _logger.LogWarning("Kubernetes inventory projection lost its lock and will wait to acquire it again");

                return;
            }

            bool waitBeforeNextPoll;
            try
            {
                waitBeforeNextPoll = await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kubernetes inventory projection poll failed, will retry after {Delay}", PollInterval);
                waitBeforeNextPoll = true;
            }

            if (waitBeforeNextPoll == true)
            {
                await Task.Delay(PollInterval, _timeProvider, ct);
            }
        }
    }

    /// <summary>
    /// Reads and applies one batch, then collects garbage when it is due.
    /// </summary>
    /// <returns>True when the caller should wait before polling again; false when more rows may be waiting.</returns>
    private async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        IReadOnlyList<MachineTelemetry> batch;
        using (IServiceScope scope = _scopeFactory.CreateScope())
        {
            IKubernetesInventoryRepository repository = scope.ServiceProvider.GetRequiredService<IKubernetesInventoryRepository>();

            long cursor = await repository.GetProjectionCursorAsync(ct) ?? 0;
            batch = await repository.GetKubernetesTelemetryBatchAsync(
                cursor, now - ReadWindow, now - _visibilityLag, BatchSize, ct);
        }

        bool waitBeforeNextPoll = batch.Count == 0;
        foreach (MachineTelemetry row in batch)
        {
            if (await ProcessRowAsync(row, ct) == true)
            {
                waitBeforeNextPoll = true;

                break;
            }
        }

        await CollectGarbageWhenDueAsync(ct);

        return waitBeforeNextPoll;
    }

    /// <summary>
    /// Applies or skips one telemetry row.
    /// </summary>
    /// <returns>True when the rest of the batch must wait because this row failed temporarily.</returns>
    private async Task<bool> ProcessRowAsync(MachineTelemetry row, CancellationToken ct)
    {
        if (row.TelemetryType != TelemetryTypeIds.KubernetesNode)
        {
            await SkipRowAsync(row.Id, ct);

            return false;
        }

        KubernetesNodePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<KubernetesNodePayload>(row.Payload, JsonDefaults.SnakeCase);
        }
        catch (JsonException ex)
        {
            await SkipInvalidPayloadAsync(row, "the payload is not valid JSON for a node report", ex, ct);

            return false;
        }

        if (payload is null)
        {
            await SkipInvalidPayloadAsync(row, "the payload is empty", null, ct);

            return false;
        }

        LocalNodeReport? report = NormalisePayload(payload, row);
        if (report is null)
        {
            await SkipInvalidPayloadAsync(row, "the payload names no distro or its node name is not a valid Kubernetes node name", null, ct);

            return false;
        }

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IKubernetesInventoryRepository repository = scope.ServiceProvider.GetRequiredService<IKubernetesInventoryRepository>();

            LocalReportOutcome outcome = await repository.ApplyLocalNodeReportAsync(report, _timeProvider.GetUtcNow(), ct);
            ClearRetryState();
            _logger.LogDebug("Kubernetes node report {TelemetryId} applied with outcome {Outcome}", row.Id, outcome);

            return false;
        }
        catch (Exception ex) when (ct.IsCancellationRequested == false)
        {
            if (KubernetesProjectionErrors.IsTransient(ex) == true)
            {
                return await HandleTransientFailureAsync(row, ex, ct);
            }

            // The row only counts as skipped once the cursor has moved past it. If that write fails the
            // row is read again, and counting it first would count the same row on every poll.
            await SkipRowAsync(row.Id, ct);
            ClearRetryState();
            _metrics.RecordProjectionFailure(KubernetesProjectionFailureReason.ApplyFailed);
            _logger.LogError(ex, "Kubernetes node report {TelemetryId} could not be applied and was skipped", row.Id);

            return false;
        }
    }

    /// <summary>
    /// Counts one more temporary failure of a row. Until the budget is spent the row stays where it is
    /// for the next poll; once it is spent the row is skipped so it cannot hold the stream back forever.
    /// </summary>
    /// <returns>True when the row stays for another attempt; false when it was skipped.</returns>
    private async Task<bool> HandleTransientFailureAsync(MachineTelemetry row, Exception exception, CancellationToken ct)
    {
        _retryAttempts = (_retryTelemetryId == row.Id) ? (_retryAttempts + 1) : 1;
        _retryTelemetryId = row.Id;

        if (_retryAttempts < MaxTransientAttempts)
        {
            _logger.LogWarning(
                exception,
                "Kubernetes node report {TelemetryId} hit a temporary error on attempt {Attempt} of {MaxAttempts}, retrying after {Delay}",
                row.Id, _retryAttempts, MaxTransientAttempts, PollInterval);

            return true;
        }

        // Counted only once the cursor has moved: a failed write leaves the attempt count spent, so the
        // next poll tries the row once more and gives up on it again.
        await SkipRowAsync(row.Id, ct);
        ClearRetryState();
        _metrics.RecordProjectionFailure(KubernetesProjectionFailureReason.RetriesExhausted);
        _logger.LogError(
            exception,
            "Kubernetes node report {TelemetryId} still failed after {MaxAttempts} attempts and was skipped",
            row.Id, MaxTransientAttempts);

        return false;
    }

    private async Task SkipInvalidPayloadAsync(MachineTelemetry row, string reason, Exception? exception, CancellationToken ct)
    {
        await SkipRowAsync(row.Id, ct);
        ClearRetryState();
        _metrics.RecordProjectionFailure(KubernetesProjectionFailureReason.InvalidPayload);
        _logger.LogError(exception, "Kubernetes node report {TelemetryId} was skipped: {Reason}", row.Id, reason);
    }

    /// <summary>
    /// Moves the cursor past a row that is not applied, in a write of its own.
    /// </summary>
    private async Task SkipRowAsync(long telemetryId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IKubernetesInventoryRepository repository = scope.ServiceProvider.GetRequiredService<IKubernetesInventoryRepository>();

        await repository.SetProjectionCursorAsync(telemetryId, _timeProvider.GetUtcNow(), ct);
    }

    /// <summary>
    /// Runs garbage collection on the first poll after the lock is taken and then once per
    /// <see cref="GcInterval"/>. The attempt time is recorded before the call so a failing collection
    /// is not retried on every poll, and a failure never reaches the projection.
    /// </summary>
    private async Task CollectGarbageWhenDueAsync(CancellationToken ct)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if ((_lastGarbageCollection is not null) && ((now - _lastGarbageCollection.Value) < GcInterval))
        {
            return;
        }

        _lastGarbageCollection = now;

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IKubernetesInventoryRepository repository = scope.ServiceProvider.GetRequiredService<IKubernetesInventoryRepository>();

            await repository.CollectGarbageAsync(now, ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested == false)
        {
            _logger.LogError(ex, "Kubernetes inventory garbage collection failed, will try again after {Interval}", GcInterval);
        }
    }

    private void ClearRetryState()
    {
        _retryTelemetryId = null;
        _retryAttempts = 0;
    }
}
