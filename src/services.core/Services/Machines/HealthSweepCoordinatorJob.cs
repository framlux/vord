// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Recurring Hangfire job that enumerates every tenant with active machine state and enqueues a
/// fire-and-forget <see cref="HealthSweepTenantJob"/> per tenant. Replaces the iterate-all-tenants
/// loop in the former <c>HealthSweepService</c>; the actual per-tenant work is fanned out across
/// Hangfire workers, so multiple replicas process different tenants concurrently.
/// </summary>
/// <remarks>
/// Hangfire's recurring schedule is minute-granularity. To restore the predecessor's 30-second
/// sweep cadence, <see cref="RunAsync"/> performs an immediate fan-out and also schedules a
/// second fan-out 30 seconds later via the Hangfire <c>IBackgroundJobClient.Schedule</c>
/// extension.
/// </remarks>
public sealed class HealthSweepCoordinatorJob
{
    /// <summary>
    /// Sub-minute offset for the secondary fan-out scheduled within each recurring tick.
    /// </summary>
    private static readonly TimeSpan SecondaryFanOutDelay = TimeSpan.FromSeconds(30);

    private readonly IMachineStateRepository _machineStateRepository;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<HealthSweepCoordinatorJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="HealthSweepCoordinatorJob"/> class.
    /// </summary>
    /// <param name="machineStateRepository">Source of the distinct active tenants list.</param>
    /// <param name="backgroundJobClient">Hangfire client used to enqueue per-tenant jobs.</param>
    /// <param name="logger">The logger.</param>
    public HealthSweepCoordinatorJob(
        IMachineStateRepository machineStateRepository,
        IBackgroundJobClient backgroundJobClient,
        ILogger<HealthSweepCoordinatorJob> logger)
    {
        ArgumentNullException.ThrowIfNull(machineStateRepository);
        ArgumentNullException.ThrowIfNull(backgroundJobClient);
        ArgumentNullException.ThrowIfNull(logger);

        _machineStateRepository = machineStateRepository;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    /// <summary>
    /// Recurring entry point. Schedules the +30 s fan-out FIRST so a failure in the immediate
    /// fan-out does not silently downgrade cadence from 30 s to 60 s for that tick. The schedule
    /// call is wrapped in a try/catch — a Hangfire-storage hiccup writing the scheduled job
    /// should NOT poison this minute's primary fan-out.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [DisableConcurrentExecution(timeoutInSeconds: 30)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("critical")]
    public async Task RunAsync(CancellationToken ct)
    {
        TryScheduleSecondaryFanOut();
        await FanOutAsync(ct);
    }

    /// <summary>
    /// Best-effort schedule of the +30 s fan-out. Failures are logged but never bubble out so
    /// the primary FanOutAsync still runs even when Hangfire storage is briefly unavailable.
    /// </summary>
    private void TryScheduleSecondaryFanOut()
    {
        try
        {
            _backgroundJobClient.Schedule<HealthSweepCoordinatorJob>(
                job => job.FanOutAsync(CancellationToken.None),
                SecondaryFanOutDelay);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to schedule secondary HealthSweep fan-out; this tick will run only the immediate fan-out");
        }
    }

    /// <summary>
    /// Enumerates active tenants and enqueues a per-tenant health sweep for each. Exposed for
    /// the +30 s scheduled invocation that <see cref="RunAsync"/> queues.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [DisableConcurrentExecution(timeoutInSeconds: 30)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("critical")]
    public async Task FanOutAsync(CancellationToken ct)
    {
        List<int> tenantIds = await _machineStateRepository.GetDistinctTenantIdsAsync(ct);

        foreach (int tenantId in tenantIds)
        {
            try
            {
                _backgroundJobClient.Enqueue<HealthSweepTenantJob>(job => job.RunAsync(tenantId, CancellationToken.None));
            }
            catch (Exception ex)
            {
                // A transient enqueue failure for one tenant must not prevent enqueueing the rest.
                // The next coordinator tick will try again for any tenant whose enqueue failed here.
                _logger.LogWarning(ex, "Failed to enqueue HealthSweepTenantJob for tenant {TenantId}; will retry on next coordinator tick", tenantId);
            }
        }
    }
}
