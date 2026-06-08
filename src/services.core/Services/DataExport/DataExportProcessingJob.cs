// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Handlers;
using Hangfire;

namespace Framlux.FleetManagement.Services.Core.DataExport;

/// <summary>
/// Hangfire recurring + on-demand job that processes pending tenant data export requests.
/// Replaces the former DataExportBackgroundService. The recurring tick scans for Pending and
/// orphan-Processing rows; the request endpoint enqueues <see cref="ProcessSingleAsync"/> for
/// sub-minute pickup of newly-requested exports.
/// </summary>
public sealed class DataExportProcessingJob
{
    /// <summary>
    /// Maximum time a single export is permitted to spend in Processing before the orphan reaper
    /// treats it as stuck and resets it back to Pending. Must be greater than the
    /// <see cref="DisableConcurrentExecutionAttribute"/> timeout below so a slow-but-running job
    /// is not reset out from under itself.
    /// </summary>
    private static readonly TimeSpan StuckProcessingThreshold = TimeSpan.FromHours(1);

    private readonly IDataExportRepository _dataExportRepository;
    private readonly IDataExportHandler _dataExportHandler;
    private readonly ILogger<DataExportProcessingJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="DataExportProcessingJob"/> class.
    /// </summary>
    /// <param name="dataExportRepository">Source of pending export-job rows.</param>
    /// <param name="dataExportHandler">Handler that materializes and uploads the export file.</param>
    /// <param name="logger">The logger.</param>
    public DataExportProcessingJob(
        IDataExportRepository dataExportRepository,
        IDataExportHandler dataExportHandler,
        ILogger<DataExportProcessingJob> logger)
    {
        ArgumentNullException.ThrowIfNull(dataExportRepository);
        ArgumentNullException.ThrowIfNull(dataExportHandler);
        ArgumentNullException.ThrowIfNull(logger);

        _dataExportRepository = dataExportRepository;
        _dataExportHandler = dataExportHandler;
        _logger = logger;
    }

    /// <summary>
    /// Processes every pending export job. First sweeps orphan Processing rows back to Pending,
    /// then claims and processes each Pending row atomically. Concurrent workers cannot
    /// double-claim a job (the claim is a conditional UPDATE on Status=Pending).
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("long")]
    public async Task RunAsync(CancellationToken ct)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - StuckProcessingThreshold;
        List<DataExportJob> stuck = await _dataExportRepository.GetStuckProcessingJobsAsync(cutoff, ct);
        foreach (DataExportJob orphan in stuck)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            _logger.LogWarning("Resetting orphaned export job {JobId} (StartedAt={StartedAt}); next pass will pick it up",
                orphan.Id, orphan.StartedAt);
            await _dataExportRepository.ResetOrphanedJobToPendingAsync(orphan.Id, ct);
        }

        List<DataExportJob> pendingJobs = await _dataExportRepository.GetPendingExportJobsAsync(ct);

        foreach (DataExportJob job in pendingJobs)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await ProcessSingleAsync(job.Id, ct);
        }
    }

    /// <summary>
    /// Processes a single export job identified by id. Used by the data-export create endpoint
    /// to enqueue exactly the row it just inserted, rather than a fleet-wide sweep. The job
    /// claims the row atomically; if another worker already claimed it (or the row was never
    /// Pending) the call exits cleanly so Hangfire records it as Succeeded.
    /// </summary>
    /// <param name="jobId">The export job id to process.</param>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [AutomaticRetry(Attempts = 0)]
    [Queue("long")]
    public async Task ProcessSingleAsync(int jobId, CancellationToken ct)
    {
        if (jobId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(jobId), "Job id must be positive.");
        }

        bool claimed = await _dataExportRepository.TryClaimPendingJobAsync(jobId, DateTimeOffset.UtcNow, ct);
        if (claimed == false)
        {
            _logger.LogDebug("Export job {JobId} was not in Pending state; skipping", jobId);

            return;
        }

        _logger.LogInformation("Processing data export job {JobId}", jobId);

        try
        {
            await _dataExportHandler.ProcessExportJobAsync(jobId, ct);
        }
        catch (Exception ex)
        {
            // The handler's own failure path persists Failed status via the repository. If something
            // escapes (e.g. a transport-level error before the handler can record it), increment
            // the failure counter and either reset to Pending (under the retry budget) or mark
            // Failed (budget exhausted) so a permanently broken job no longer cycles.
            int attempts = 0;
            try
            {
                attempts = await _dataExportRepository.IncrementFailureCountAsync(jobId, CancellationToken.None);
            }
            catch (Exception countEx)
            {
                // Failure-count update is best-effort; do not mask the original exception.
                _logger.LogWarning(countEx, "Failed to increment FailureCount for export job {JobId}", jobId);
            }

            if (attempts >= MaxFailureAttempts)
            {
                _logger.LogError(
                    ex,
                    "Export job {JobId} exhausted retry budget ({Attempts}/{Max}); marking Failed",
                    jobId, attempts, MaxFailureAttempts);

                try
                {
                    await _dataExportRepository.MarkExportJobFailedAsync(
                        jobId,
                        $"Exhausted {MaxFailureAttempts} attempts; last error: {ex.Message}",
                        CancellationToken.None);
                }
                catch (Exception markEx)
                {
                    _logger.LogError(markEx, "Failed to mark export job {JobId} as Failed", jobId);
                }

                throw;
            }

            _logger.LogError(ex, "Export job {JobId} processing failed (attempt {Attempts}); resetting to Pending", jobId, attempts);

            try
            {
                await _dataExportRepository.ResetOrphanedJobToPendingAsync(jobId, CancellationToken.None);
            }
            catch (Exception resetEx)
            {
                // The original handler exception is the actionable root cause; do NOT let a reset
                // failure mask it. Wrap both so the operator sees the real failure in the dashboard
                // while still surfacing that the recovery step also failed (the orphan reaper will
                // reset this row on its next pass).
                _logger.LogError(resetEx, "Failed to reset export job {JobId} to Pending after handler failure", jobId);
                throw new AggregateException(ex, resetEx);
            }

            throw;
        }
    }

    /// <summary>
    /// Maximum number of processing attempts before a job transitions to <c>Failed</c>. After
    /// this many failures the row stops being re-claimed by the recurring tick, so a
    /// permanently broken job no longer generates one Failed Hangfire entry per minute.
    /// </summary>
    internal const int MaxFailureAttempts = 5;
}
