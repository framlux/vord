// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Shared constants for the alert subsystem including Redis key prefixes and well-known values.
/// </summary>
public static class AlertConstants
{
    // Note: the former DeliveryQueueKey ("alert:delivery:queue") and DeliveryDeadLetterKey
    // ("alert:delivery:deadletter") constants were removed when integration delivery migrated to
    // Hangfire's native enqueue + [AutomaticRetry]; failed deliveries land in the Hangfire
    // dashboard's Failed tab. The former ConditionKeyPrefix ("alert:condition") was removed when
    // AlertEvaluation migrated to the AlertConditionStates Postgres table.

    /// <summary>The MachineStateSummary.HealthStatus value that represents an offline machine.</summary>
    public const short HealthStatusOffline = 3;

    /// <summary>
    /// Redis key prefix for the failed-SSH-login chain lease. One fenced lease per machine, held for
    /// as long as an evaluation chain is running for it.
    /// </summary>
    public const string FailedSshLoginChainKeyPrefix = "alert:sshfail:chain";

    /// <summary>
    /// Length of the platform's failed-SSH-login evaluation cadence, and the minimum window a rule
    /// for that metric may be configured with.
    /// </summary>
    /// <remarks>
    /// This is not a per-rule window and must never become one. Each rule's own DurationMinutes is
    /// applied by the evaluation, which counts that rule's window from telemetry rows, so a built-in
    /// watching five minutes and a custom rule watching sixty are both answered by one run.
    /// <para>
    /// A rule may configure a window longer than this and is still answered correctly, because every
    /// run counts that rule's window back from the moment it runs. A window *shorter* than this is
    /// refused — see <see cref="GetMinimumDurationMinutes"/> — because evaluations happen at this
    /// cadence, so a one-minute rule would only ever inspect one minute in every five and silently
    /// miss an attack that ended in the other four.
    /// </para>
    /// </remarks>
    public const int FailedSshLoginWindowMinutes = 5;

    /// <summary>
    /// <see cref="FailedSshLoginWindowMinutes"/> as a <see cref="TimeSpan"/>.
    /// </summary>
    public static TimeSpan FailedSshLoginWindow => TimeSpan.FromMinutes(FailedSshLoginWindowMinutes);

    /// <summary>
    /// How long a failed-SSH-login chain lease survives without being renewed.
    /// </summary>
    /// <remarks>
    /// Comfortably longer than the re-arm cadence, because a chain that renews every window must
    /// never lose its lease to ordinary queue latency and let a second chain start alongside it. A
    /// chain that ends releases its lease explicitly, so this expiry is only the backstop for a run
    /// that was lost outright.
    /// </remarks>
    public static TimeSpan FailedSshLoginChainLease => TimeSpan.FromMinutes(FailedSshLoginWindowMinutes * 3);

    /// <summary>
    /// The most an evaluation may widen its window backwards to cover the delay between the window
    /// being opened and the scheduled job actually running.
    /// </summary>
    /// <remarks>
    /// A job scheduled for one window from now runs at that instant plus Hangfire's polling and
    /// pickup latency. Measuring the window purely back from the run instant therefore excludes the
    /// very telemetry that opened the window — a burst delivered in a single envelope carries one
    /// server receipt timestamp, so it falls out of the window entirely and the incident is never
    /// seen. The evaluation anchors the window to the moment it was opened instead; this bounds how
    /// far back a badly delayed job may reach, so a run recovered from a long backlog cannot count
    /// hours of attempts against a five-minute rule.
    /// </remarks>
    public static TimeSpan FailedSshLoginMaxWindowSlack => TimeSpan.FromMinutes(FailedSshLoginWindowMinutes);

    /// <summary>
    /// The substring a stored SSH-session payload contains when the attempt failed authentication.
    /// </summary>
    /// <remarks>
    /// Payloads are stored as text and counted with a substring match so the database can answer
    /// "how many failures in this window" as one aggregate, rather than shipping thousands of rows
    /// to be deserialized under a brute-force rate. That makes the serialized shape a contract: the
    /// marker is spelled exactly as the snake-case serializer emits it, with no spaces, and a test
    /// pins it against a real serialization so a serializer change fails loudly instead of silently
    /// counting zero failures forever.
    /// </remarks>
    public const string FailedSshLoginPayloadMarker = "\"action\":\"failed\"";

    /// <summary>
    /// Maximum number of failed-login payloads read back to describe an incident once the count has
    /// already decided one is happening. The aggregates in the alert's details are drawn from this
    /// sample, newest first, and the details say how many attempts the sample covered.
    /// </summary>
    public const int FailedSshLoginDetailSampleLimit = 500;

    /// <summary>
    /// Maximum number of distinct user names listed in a failed-login alert's details.
    /// </summary>
    public const int FailedSshLoginDetailUserLimit = 10;

    /// <summary>
    /// Maximum DurationMinutes an alert rule can configure. Validators must enforce this upper
    /// bound; <see cref="ConditionStateRetentionWindow"/> sizes the AlertConditionStates
    /// reaper window accordingly. Raising this constant requires reviewing the safety margin.
    /// </summary>
    public const int MaxRuleDurationMinutes = 1440;

    /// <summary>
    /// Safety margin added to <see cref="MaxRuleDurationMinutes"/> when sizing the
    /// AlertConditionStates retention window. Prevents the reaper from deleting a row mid-window
    /// even under realistic clock drift between application and database.
    /// </summary>
    public const int ConditionStateRetentionSafetyMarginMinutes = 15;

    /// <summary>
    /// Retention window for <c>AlertConditionStates</c> rows — kept strictly above the largest
    /// DurationMinutes a rule can configure so the reaper never deletes an in-progress window.
    /// </summary>
    public static TimeSpan ConditionStateRetentionWindow
        => TimeSpan.FromMinutes(MaxRuleDurationMinutes + ConditionStateRetentionSafetyMarginMinutes);

    /// <summary>
    /// Returns the minimum allowed DurationMinutes for a given alert metric.
    /// Volatile metrics (CPU, Memory, Disk) require a sustained condition.
    /// State metrics require a shorter minimum. Point-in-time metrics require zero.
    /// </summary>
    public static int GetMinimumDurationMinutes(AlertMetric metric)
    {
        return metric switch
        {
            AlertMetric.CpuUsage => 5,
            AlertMetric.MemoryUsage => 5,
            AlertMetric.DiskUsage => 5,
            AlertMetric.MachineOffline => 1,
            AlertMetric.FailedServices => 1,
            AlertMetric.SecurityUpdates => 1,
            AlertMetric.DiskHealth => 1,
            AlertMetric.SshConnection => 0,
            AlertMetric.TelemetryStale => 1,
            // A failed-login rule is evaluated on the platform cadence, so a window shorter than that
            // cadence would leave most of the traffic uninspected — a tighter rule would detect less
            // than the built-in, which is the opposite of what configuring one means.
            AlertMetric.FailedSshLogin => FailedSshLoginWindowMinutes,
            _ => 1,
        };
    }

    /// <summary>
    /// Returns true if the metric is evaluated at telemetry ingestion time rather than in the
    /// periodic evaluation loop.
    /// </summary>
    /// <remarks>
    /// This answers "where does evaluation happen", and nothing else. The periodic loop reads its
    /// values off a MachineStateSummary row; a metric that has no field there cannot be evaluated
    /// by it at all, so the loop skips every metric this returns true for and the ingest path owns
    /// them instead.
    /// <para>
    /// This is deliberately NOT the same question as <see cref="RequiresZeroDuration"/>. It used to
    /// be, back when the only ingest-evaluated metric was a single SSH connection, and the two
    /// meanings were indistinguishable. They are not the same thing: a count of failed logins is
    /// also evaluated at ingest, yet it is meaningful only over a window — "more than five in five
    /// minutes" is the whole alert. Re-merging the two would force every ingest-evaluated metric to
    /// carry a zero duration and silently destroy per-rule windows.
    /// </para>
    /// </remarks>
    public static bool IsEventMetric(AlertMetric metric)
    {
        return metric switch
        {
            AlertMetric.SshConnection => true,
            AlertMetric.FailedSshLogin => true,
            _ => false,
        };
    }

    /// <summary>
    /// Returns true if the metric describes a point-in-time occurrence, so a duration window is
    /// meaningless and the configured DurationMinutes must be zero.
    /// </summary>
    /// <remarks>
    /// A single SSH connection either happened or it did not; there is no state for it to persist
    /// in across a window, so asking for "a connection sustained for five minutes" is incoherent.
    /// Windowed metrics — including ingest-evaluated ones such as a failed-login count — answer
    /// this false, because the window is exactly what they measure over. See
    /// <see cref="IsEventMetric"/> for why the two predicates must stay separate.
    /// </remarks>
    public static bool RequiresZeroDuration(AlertMetric metric)
    {
        return metric switch
        {
            AlertMetric.SshConnection => true,
            _ => false,
        };
    }
}
