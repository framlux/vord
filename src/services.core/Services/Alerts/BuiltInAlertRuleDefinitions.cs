// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// The built-in alert rules every tenant is provisioned with — one per <see cref="AlertMetric"/>.
/// </summary>
/// <remarks>
/// <para>
/// The durations here are tuned for a rule nobody chose and nobody can retune below the Team tier.
/// A built-in that fires during normal operation trains people to ignore every alert the product
/// sends, so each duration is set at the point where the condition stops being ordinary system
/// behaviour rather than at the point where it becomes technically true.
/// </para>
/// <para>
/// Changing a duration or threshold here only affects tenants provisioned afterwards. Provisioning
/// never mutates rows that already exist, so an established tenant keeps whatever it was seeded
/// with.
/// </para>
/// </remarks>
public static class BuiltInAlertRuleDefinitions
{
    /// <summary>
    /// The seeded <c>system</c> user that owns built-in rules. <c>AlertRules.CreatedByUserId</c>
    /// carries a foreign key to <c>Users</c>, so a rule authored by nobody still needs a real row
    /// to point at.
    /// </summary>
    public const int SystemUserId = 1;

    /// <summary>
    /// Every built-in definition, exactly one per metric.
    /// </summary>
    public static IReadOnlyList<BuiltInAlertRuleDefinition> All { get; } =
    [
        // The offline flag already requires five minutes of silence (OnlineThresholdSeconds = 300),
        // so ten more means roughly fifteen minutes dark before anyone is paged — long enough to
        // survive a reboot or a brief network loss without a false alarm.
        new BuiltInAlertRuleDefinition(
            "Machine offline",
            AlertMetric.MachineOffline,
            AlertOperator.EqualTo,
            1,
            10,
            AlertSeverity.Critical),

        // A SMART pre-fail is never noise and the window before data loss is short, so this fires
        // at the first sustained reading.
        new BuiltInAlertRuleDefinition(
            "Disk health issues",
            AlertMetric.DiskHealth,
            AlertOperator.EqualTo,
            1,
            1,
            AlertSeverity.Critical),

        // Disk fills slowly and the remedy is manual, so five minutes is already enough to suppress
        // build and log-rotation spikes without delaying anything actionable.
        new BuiltInAlertRuleDefinition(
            "Disk usage above 90%",
            AlertMetric.DiskUsage,
            AlertOperator.GreaterThan,
            90,
            5,
            AlertSeverity.Warning),

        // Five minutes rides out boot-order races, where a unit fails once and its dependency
        // restarts it without anyone needing to know.
        new BuiltInAlertRuleDefinition(
            "Failed services detected",
            AlertMetric.FailedServices,
            AlertOperator.GreaterThan,
            0,
            5,
            AlertSeverity.Warning),

        // Below fifteen minutes this flags every build, backup and batch job. An alert that fires
        // during normal operation is worse than no alert, because it teaches people to dismiss the
        // ones that matter.
        new BuiltInAlertRuleDefinition(
            "CPU usage above 90%",
            AlertMetric.CpuUsage,
            AlertOperator.GreaterThan,
            90,
            15,
            AlertSeverity.Warning),

        // Same reasoning as CPU, and Linux deliberately runs memory high — cache and buffers make a
        // sustained high reading the normal state of a healthy machine.
        new BuiltInAlertRuleDefinition(
            "Memory usage above 90%",
            AlertMetric.MemoryUsage,
            AlertOperator.GreaterThan,
            90,
            15,
            AlertSeverity.Warning),

        // This earns its place only because delivery de-duplicates per rule and machine, so a
        // standing set of pending updates produces one notification per episode rather than one per
        // evaluation pass.
        new BuiltInAlertRuleDefinition(
            "Security updates available",
            AlertMetric.SecurityUpdates,
            AlertOperator.GreaterThan,
            0,
            1,
            AlertSeverity.Info),

        // An event metric, evaluated at ingestion rather than in the periodic loop. There is no
        // window for a point-in-time event to persist across, so the duration must be zero.
        new BuiltInAlertRuleDefinition(
            "New SSH connection",
            AlertMetric.SshConnection,
            AlertOperator.EqualTo,
            1,
            0,
            AlertSeverity.Info),

        // A reachable machine that has stopped reporting is invisible rather than gone: the agent
        // answers, so "Machine offline" never fires, and the metrics on screen are frozen at
        // whatever they last were. Ten minutes past the staleness window is long enough to ride out
        // a collector restart. Warning, not Critical — nothing is known to be wrong, we have simply
        // stopped being able to tell.
        new BuiltInAlertRuleDefinition(
            "Telemetry stopped",
            AlertMetric.TelemetryStale,
            AlertOperator.EqualTo,
            1,
            10,
            AlertSeverity.Warning),
    ];
}
