// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Derives what a cluster's reporting looks like from the state the projection stored. Nothing here is
/// stored: a snapshot goes stale by the clock alone, so every value is worked out when it is read.
/// </summary>
public static class KubernetesReporting
{
    /// <summary>
    /// Works out the cluster's reporting state. The first test that holds decides: no candidates is
    /// <see cref="ReportingState.Off"/>, an error on the cluster or on any candidate is
    /// <see cref="ReportingState.Error"/>, outdated permissions are <see cref="ReportingState.RoleOutdated"/>,
    /// a missing or old snapshot is <see cref="ReportingState.Stale"/>, and anything else is
    /// <see cref="ReportingState.Active"/>.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="candidates">The cluster's reporter candidates.</param>
    /// <param name="now">The time staleness is measured from.</param>
    /// <returns>The reporting state.</returns>
    public static ReportingState Derive(KubernetesCluster cluster, IReadOnlyCollection<KubernetesReporterCandidate> candidates, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return ReportingState.Off;
        }

        if (HasError(cluster, candidates))
        {
            return ReportingState.Error;
        }

        if (cluster.RoleOutdated)
        {
            return ReportingState.RoleOutdated;
        }

        if (IsStale(cluster, candidates, now))
        {
            return ReportingState.Stale;
        }

        return ReportingState.Active;
    }

    /// <summary>
    /// Whether the cluster's node values are as of an earlier time than they should be: reporters are enrolled
    /// but the latest snapshot is missing or older than <see cref="KubernetesReportingPolicy.StaleThreshold"/>.
    /// It is judged on its own, whatever else is wrong with the cluster, because an error or outdated
    /// permissions do not make the values any fresher.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="candidates">The cluster's reporter candidates.</param>
    /// <param name="now">The time staleness is measured from.</param>
    /// <returns>True when the cluster has candidates and no snapshot from the last stale threshold.</returns>
    public static bool IsStale(KubernetesCluster cluster, IReadOnlyCollection<KubernetesReporterCandidate> candidates, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return false;
        }

        return IsSnapshotStale(cluster.LastSnapshotReceivedAt, now);
    }

    /// <summary>
    /// Whether a machine is the cluster's active reporter: it sent the cluster's most recent accepted snapshot
    /// and that snapshot is not stale. The server cannot see the Lease, so this is the closest it can come to
    /// saying the machine holds it now. A snapshot exactly <see cref="KubernetesReportingPolicy.StaleThreshold"/>
    /// old is not stale, the same boundary <see cref="IsStale"/> uses.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="machineId">The machine.</param>
    /// <param name="now">The time staleness is measured from.</param>
    /// <returns>True when the machine sent the latest snapshot and it is not stale.</returns>
    public static bool IsActiveReporter(KubernetesCluster cluster, long machineId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        return (cluster.ActiveReporterMachineId == machineId) && (IsSnapshotStale(cluster.LastSnapshotReceivedAt, now) == false);
    }

    private static bool IsSnapshotStale(DateTimeOffset? lastSnapshotReceivedAt, DateTimeOffset now)
    {
        return (lastSnapshotReceivedAt is null) || ((now - lastSnapshotReceivedAt.Value) > KubernetesReportingPolicy.StaleThreshold);
    }

    /// <summary>
    /// The error to show for the cluster: the one recorded on the cluster itself, else the error of the first
    /// failing candidate by machine name. The result is non-null exactly when
    /// <see cref="Derive"/> returns <see cref="ReportingState.Error"/>, and null while nobody is enrolled.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="candidates">The cluster's reporter candidates.</param>
    /// <param name="machineNames">The candidates' machine names by machine id.</param>
    /// <returns>The error code, or null when there is none to show.</returns>
    public static string? ReporterErrorOf(
        KubernetesCluster cluster, IReadOnlyCollection<KubernetesReporterCandidate> candidates, IReadOnlyDictionary<long, string> machineNames)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(machineNames);

        if (candidates.Count == 0)
        {
            return null;
        }

        if (HasValue(cluster.ReporterError))
        {
            return cluster.ReporterError;
        }

        // Names are compared ordinally, with the machine id breaking a tie, so every replica reports the
        // same candidate's error whatever order the rows came back in.
        KubernetesReporterCandidate? first = null;
        string firstName = string.Empty;
        foreach (KubernetesReporterCandidate candidate in candidates)
        {
            if (HasValue(candidate.ReporterError) == false)
            {
                continue;
            }

            string name = machineNames.GetValueOrDefault(candidate.MachineId) ?? string.Empty;
            int order = (first is null) ? -1 : string.CompareOrdinal(name, firstName);
            if ((order < 0) || ((order == 0) && (candidate.MachineId < first!.MachineId)))
            {
                first = candidate;
                firstName = name;
            }
        }

        return first?.ReporterError;
    }

    /// <summary>
    /// The value the API reports for a state.
    /// </summary>
    /// <param name="state">The reporting state.</param>
    /// <returns>One of <c>off</c>, <c>error</c>, <c>role_outdated</c>, <c>stale</c> or <c>active</c>.</returns>
    public static string ToWireValue(ReportingState state)
    {
        return state switch
        {
            ReportingState.Off => "off",
            ReportingState.Error => "error",
            ReportingState.RoleOutdated => "role_outdated",
            ReportingState.Stale => "stale",
            ReportingState.Active => "active",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown reporting state"),
        };
    }

    private static bool HasError(KubernetesCluster cluster, IReadOnlyCollection<KubernetesReporterCandidate> candidates)
    {
        return HasValue(cluster.ReporterError) || candidates.Any(c => HasValue(c.ReporterError));
    }

    private static bool HasValue(string? text)
    {
        return string.IsNullOrEmpty(text) == false;
    }
}
