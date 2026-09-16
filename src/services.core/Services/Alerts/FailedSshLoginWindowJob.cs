// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Hangfire;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Evaluates the failed-SSH-login window for one machine and, while an incident is still live,
/// schedules its own successor. Scheduled by the ingest path when a chain opens, and thereafter by
/// itself until a window comes back quiet.
/// </summary>
/// <remarks>
/// The job takes a machine rather than an attempt. A failed login is a rate, not an occurrence:
/// under brute force one job per attempt would flood the Postgres-backed critical queue, which is
/// precisely why the per-item <see cref="SshAlertEvaluationJob"/> refuses to evaluate the action.
/// <para>
/// There is one class and no mode flag, because the opening run and the trailing runs do the same
/// thing: count each rule's own window back from now, fire what is over threshold, resolve what is
/// quiet. A flag would imply a difference that does not exist.
/// </para>
/// <para>
/// Re-arming is load-bearing rather than tidy. An alert event is created only while no unresolved
/// event exists for the rule and machine, so a chain that stops while failures are still arriving
/// would leave the event Triggered forever and the de-duplication would swallow every later
/// incident silently. Only a window with no failures ends the chain, by resolving.
/// </para>
/// <para>
/// Re-arming and the ingest path share one lease, and that is what makes the chain safe. Both ask
/// <see cref="IFailedSshLoginChainGate"/> the same question, so a machine under continuous attack
/// carries exactly one chain rather than accumulating one per window from each scheduler
/// independently — the critical-queue flood the whole design exists to avoid.
/// </para>
/// </remarks>
public sealed class FailedSshLoginWindowJob
{
    /// <summary>The SSH action the agent emits for every failed authentication attempt.</summary>
    public const string FailedAction = "failed";

    /// <summary>Returns true when the given SSH action is a failed authentication attempt.</summary>
    /// <param name="action">The SSH session action.</param>
    /// <returns><c>true</c> when the action is a failed attempt; otherwise <c>false</c>.</returns>
    public static bool IsFailedAction(string action) => string.Equals(action, FailedAction, StringComparison.OrdinalIgnoreCase);

    private readonly IEventAlertService _eventAlertService;
    private readonly IFailedSshLoginChainGate _chainGate;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ResilienceMetrics _resilienceMetrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FailedSshLoginWindowJob> _logger;

    /// <summary>Creates a new instance of the <see cref="FailedSshLoginWindowJob"/> class.</summary>
    /// <param name="eventAlertService">The evaluator that counts each rule's window.</param>
    /// <param name="chainGate">The lease shared with the ingest path that keeps one chain per machine.</param>
    /// <param name="backgroundJobs">Hangfire client used to schedule the successor run.</param>
    /// <param name="resilienceMetrics">Instruments counting degraded runs of the chain lease.</param>
    /// <param name="timeProvider">Clock the successor's window is anchored to.</param>
    /// <param name="logger">Logger.</param>
    public FailedSshLoginWindowJob(
        IEventAlertService eventAlertService,
        IFailedSshLoginChainGate chainGate,
        IBackgroundJobClient backgroundJobs,
        ResilienceMetrics resilienceMetrics,
        TimeProvider timeProvider,
        ILogger<FailedSshLoginWindowJob> logger)
    {
        ArgumentNullException.ThrowIfNull(eventAlertService);
        ArgumentNullException.ThrowIfNull(chainGate);
        ArgumentNullException.ThrowIfNull(backgroundJobs);
        ArgumentNullException.ThrowIfNull(resilienceMetrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _eventAlertService = eventAlertService;
        _chainGate = chainGate;
        _backgroundJobs = backgroundJobs;
        _resilienceMetrics = resilienceMetrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Evaluates the failed-login window for one machine and re-arms if it is still live.</summary>
    /// <param name="tenantId">The tenant that owns the machine.</param>
    /// <param name="machineId">The machine whose window is evaluated.</param>
    /// <param name="windowOpenedAt">When the window this run answers was opened.</param>
    /// <param name="chainToken">The fencing token this chain holds its lease with.</param>
    /// <param name="ct">Cancellation token.</param>
    [Queue("critical")]
    public async Task RunAsync(int tenantId, long machineId, DateTimeOffset windowOpenedAt, string chainToken, CancellationToken ct)
    {
        // Captured before the evaluation so the successor's window starts no later than this one
        // ended. Consecutive windows therefore abut rather than leaving the evaluation's own runtime
        // as a gap that failures could fall into unseen.
        DateTimeOffset runAt = _timeProvider.GetUtcNow();

        bool stillLive = await _eventAlertService.EvaluateFailedSshLoginWindowAsync(tenantId, machineId, windowOpenedAt, ct);

        if (stillLive == false)
        {
            await ReleaseChainAsync(machineId, chainToken);

            return;
        }

        // The successor cadence is the platform window, not any one rule's DurationMinutes. A rule
        // with a longer window is still answered correctly, because every run counts that rule's own
        // window back from the moment it runs; a shorter window is refused by the validators, because
        // this cadence is the finest resolution an evaluation can have.
        if (await TryHoldChainAsync(machineId, chainToken) == false)
        {
            _logger.LogInformation(
                "Failed SSH login chain for machine {MachineId} has been taken over by another run; standing down",
                machineId);

            return;
        }

        _logger.LogDebug(
            "Failed SSH login window for machine {MachineId} is still live; re-arming in {WindowMinutes} minutes",
            machineId, AlertConstants.FailedSshLoginWindowMinutes);

        _backgroundJobs.Schedule<FailedSshLoginWindowJob>(
            job => job.RunAsync(tenantId, machineId, runAt, chainToken, CancellationToken.None),
            AlertConstants.FailedSshLoginWindow);
    }

    /// <summary>
    /// Extends this chain's lease, reporting whether the successor should be scheduled.
    /// </summary>
    /// <remarks>
    /// Fails open on a Redis outage. Leaving an incident with no successor would strand its event as
    /// Triggered forever and the de-duplication would then swallow every later incident on this rule
    /// and machine — a permanent hole, against a transient duplicate chain that the next successful
    /// renewal collapses back to one.
    /// </remarks>
    private async Task<bool> TryHoldChainAsync(long machineId, string chainToken)
    {
        try
        {
            return await _chainGate.RenewChainAsync(machineId, chainToken);
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            _resilienceMetrics.RecordFailOpen(ResilienceComponent.SshFailureWindow);
            _logger.LogWarning(ex,
                "Redis unavailable for the failed SSH login chain lease on machine {MachineId}; failing open and re-arming anyway",
                machineId);

            return true;
        }
    }

    /// <summary>
    /// Releases this chain's lease so the next failed login can open a new chain immediately.
    /// </summary>
    /// <remarks>
    /// A failure here is not worth retrying or surfacing as a job failure: the lease carries its own
    /// expiry, so the worst outcome is that the next incident on this machine waits out the lease
    /// instead of starting at once.
    /// </remarks>
    private async Task ReleaseChainAsync(long machineId, string chainToken)
    {
        try
        {
            await _chainGate.ReleaseChainAsync(machineId, chainToken);
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            _resilienceMetrics.RecordFailOpen(ResilienceComponent.SshFailureWindow);
            _logger.LogWarning(ex,
                "Redis unavailable while ending the failed SSH login chain for machine {MachineId}; the lease will expire on its own",
                machineId);
        }
    }
}
