// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Models.Machines;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// The status filter of the machine list and the dashboard fleet overview, parsed once so both
/// screens give the same name the same meaning.
/// </summary>
/// <remarks>
/// A filter matches against the swept health the status badge shows, never against live liveness,
/// so a row that survives a filter can never display a different badge than the one it was filtered
/// by. A paused machine keeps the health it last reported, so it matches only the paused filter.
/// </remarks>
public sealed class MachineStatusFilter
{
    private MachineStatusFilter(IReadOnlyList<MachineHealthStatus> healthStatuses, bool pausedOnly)
    {
        HealthStatuses = healthStatuses;
        PausedOnly = pausedOnly;
    }

    /// <summary>
    /// The swept health values an unpaused machine may have to match. Empty for the paused filter.
    /// </summary>
    public IReadOnlyList<MachineHealthStatus> HealthStatuses { get; }

    /// <summary>
    /// Whether the filter matches only paused machines.
    /// </summary>
    public bool PausedOnly { get; }

    /// <summary>
    /// Parses a status filter name, ignoring case.
    /// </summary>
    /// <param name="value">
    /// One of healthy, warning, critical, offline, online or paused. Online means any health other
    /// than Offline.
    /// </param>
    /// <returns>The filter, or null when the value is blank or not a recognised name.</returns>
    public static MachineStatusFilter? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.ToLowerInvariant() switch
        {
            "healthy" => new MachineStatusFilter([MachineHealthStatus.Healthy], false),
            "warning" => new MachineStatusFilter([MachineHealthStatus.Warning], false),
            "critical" => new MachineStatusFilter([MachineHealthStatus.Critical], false),
            "offline" => new MachineStatusFilter([MachineHealthStatus.Offline], false),
            "online" => new MachineStatusFilter(
                [MachineHealthStatus.Healthy, MachineHealthStatus.Warning, MachineHealthStatus.Critical], false),
            "paused" => new MachineStatusFilter([], true),
            _ => null,
        };
    }

    /// <summary>
    /// Whether a machine matches the filter.
    /// </summary>
    /// <param name="summary">The machine's summary row, or null when it has none and so reads as Offline.</param>
    /// <param name="isPaused">Whether the machine is past its tenant's machine limit.</param>
    /// <returns>True when the machine belongs under this filter.</returns>
    public bool Matches(MachineStateSummary? summary, bool isPaused)
    {
        if (PausedOnly)
        {
            return isPaused;
        }

        return (isPaused == false) && HealthStatuses.Contains(MachineLiveness.Health(summary));
    }
}
