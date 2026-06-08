// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Daily reaper that deletes stale AlertConditionState rows. A row becomes stale when its
/// LastObservedAt falls outside the retention window — typically because the machine was
/// unassigned from the rule mid-evaluation, so neither the cleared-condition nor the fired-event
/// cleanup paths ever ran on it. The FK cascade handles machine-deletion; this job handles the
/// soft-unassign case.
/// </summary>
public sealed class AlertConditionStateCleanupJob
{
    /// <summary>
    /// Retention window for AlertConditionState rows. Anything not observed within this window
    /// is presumed orphaned and reaped. Sourced from <see cref="AlertConstants.ConditionStateRetentionWindow"/>
    /// so changes to the validator's max DurationMinutes automatically propagate here.
    /// </summary>
    private static readonly TimeSpan RetentionWindow = AlertConstants.ConditionStateRetentionWindow;

    private readonly IAlertConditionStateRepository _repository;
    private readonly ILogger<AlertConditionStateCleanupJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertConditionStateCleanupJob"/> class.
    /// </summary>
    /// <param name="repository">Repository providing the bulk-delete operation.</param>
    /// <param name="logger">The logger.</param>
    public AlertConditionStateCleanupJob(
        IAlertConditionStateRepository repository,
        ILogger<AlertConditionStateCleanupJob> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Deletes AlertConditionState rows older than the retention window. Idempotent — re-running
    /// on a freshly-reaped table is a no-op.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken ct)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - RetentionWindow;
        int deleted = await _repository.DeleteStaleAsync(cutoff, ct);

        if (deleted > 0)
        {
            _logger.LogInformation("Reaped {Count} stale AlertConditionState rows older than {Cutoff}",
                deleted, cutoff);
        }
        else
        {
            _logger.LogDebug("No stale AlertConditionState rows to reap");
        }
    }
}
