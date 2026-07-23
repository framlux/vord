// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Collects the tenants whose effective retention changed during the current unit of work and enqueues
/// <see cref="RetentionReclassifyJob"/> for them once that work has committed.
/// </summary>
/// <remarks>
/// <para>
/// The enqueue must not happen inside the transaction that changes the plan. Detecting a tier change is
/// only possible at the subscription-repository seam, which runs mid-transaction, so detection and
/// dispatch are split: the seam calls <see cref="MarkPending"/>, and the caller that owns the
/// transaction calls <see cref="DispatchPending"/> immediately after its commit.
/// </para>
/// <para>
/// A caller that forgets to dispatch cannot silently lose the reclassification: this service is scoped
/// and dispatches anything still pending when the scope is disposed. Scope teardown always happens
/// after any transaction opened inside it has committed or rolled back, so the fallback is still
/// correctly ordered — it only delays the enqueue to the end of the request or job. A rolled-back
/// change dispatches a job that reads the unchanged committed retention and moves nothing, so an
/// over-eager dispatch is harmless.
/// </para>
/// </remarks>
public sealed class RetentionReclassifyDispatcher : IDisposable
{
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<RetentionReclassifyDispatcher> _logger;
    private readonly HashSet<int> _pending = [];

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetentionReclassifyDispatcher"/> class.
    /// </summary>
    /// <param name="backgroundJobs">Hangfire client used to enqueue the reclassification job.</param>
    /// <param name="logger">The logger.</param>
    public RetentionReclassifyDispatcher(
        IBackgroundJobClient backgroundJobs,
        ILogger<RetentionReclassifyDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(backgroundJobs);
        ArgumentNullException.ThrowIfNull(logger);

        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    /// <summary>
    /// Records that a tenant's effective retention changed and its surviving telemetry must be
    /// reclassified once the surrounding unit of work commits. Marking the same tenant repeatedly
    /// within one unit of work still dispatches a single job.
    /// </summary>
    /// <param name="tenantId">The tenant whose telemetry needs reclassification.</param>
    public void MarkPending(int tenantId)
    {
        _pending.Add(tenantId);
    }

    /// <summary>
    /// Enqueues the reclassification job for every tenant marked since the last dispatch. Call this
    /// immediately after the commit that made the retention change durable — never inside the
    /// transaction. A Hangfire failure is logged rather than thrown: the change has already committed
    /// and must not be failed by a queueing problem.
    /// </summary>
    public void DispatchPending()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        foreach (int tenantId in _pending)
        {
            try
            {
                _backgroundJobs.Enqueue<RetentionReclassifyJob>(job => job.RunAsync(tenantId, CancellationToken.None));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to enqueue retention reclassification for tenant {TenantId}; its telemetry keeps the previous retention class until the job is re-run",
                    tenantId);
            }
        }

        _pending.Clear();
    }

    /// <summary>
    /// Dispatches anything still pending at scope teardown, so a caller that changed a tenant's plan
    /// without dispatching explicitly still gets its reclassification.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_pending.Count == 0)
        {
            return;
        }

        _logger.LogDebug(
            "Retention reclassify: dispatching {Count} pending tenant(s) at scope teardown", _pending.Count);
        DispatchPending();
    }
}
