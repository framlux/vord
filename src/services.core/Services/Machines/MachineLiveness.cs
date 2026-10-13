// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Models.Machines;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// The single derivation of machine liveness for read paths.
/// </summary>
/// <remarks>
/// Online and healthy are not two opinions about one fact: online asks whether the server has heard
/// from a machine lately, health asks whether what it reported is good. The health sweep answers
/// the first question when it writes Offline, having looked at both the telemetry and heartbeat
/// channels, so every read path derives online from that one answer rather than consulting a
/// second clock of its own.
/// </remarks>
public static class MachineLiveness
{
    /// <summary>
    /// Whether a machine with the given swept health status is online.
    /// </summary>
    /// <param name="healthStatus">The swept HealthStatus column value.</param>
    /// <returns>True when the sweep has not declared the machine offline.</returns>
    public static bool IsOnline(short healthStatus)
    {
        return healthStatus != (short)MachineHealthStatus.Offline;
    }

    /// <summary>
    /// The swept health of a machine, treating an absent summary row as offline: a machine that has
    /// never reported has not been heard from, which is what Offline means.
    /// </summary>
    /// <param name="summary">The machine's summary row, or null when it has none.</param>
    /// <returns>The row's swept health, or Offline when the machine has no row.</returns>
    public static MachineHealthStatus Health(MachineStateSummary? summary)
    {
        if (summary is null)
        {
            return MachineHealthStatus.Offline;
        }

        return (MachineHealthStatus)summary.HealthStatus;
    }

    /// <summary>
    /// Whether a machine is online, treating an absent summary row as offline.
    /// </summary>
    /// <param name="summary">The machine's summary row, or null when it has none.</param>
    /// <returns>True when the row exists and the sweep has not declared it offline.</returns>
    public static bool IsOnline(MachineStateSummary? summary)
    {
        return IsOnline((short)Health(summary));
    }

    /// <summary>
    /// The more recent of the telemetry and heartbeat receipts, or null when neither exists.
    /// </summary>
    /// <param name="lastSeenAt">Last telemetry receipt.</param>
    /// <param name="lastHeartbeatAt">Last heartbeat receipt.</param>
    /// <returns>The later of the two receipts, or null when the server has heard nothing.</returns>
    public static DateTimeOffset? LastSeen(DateTimeOffset? lastSeenAt, DateTimeOffset? lastHeartbeatAt)
    {
        if (lastSeenAt is null)
        {
            return lastHeartbeatAt;
        }

        if (lastHeartbeatAt is null)
        {
            return lastSeenAt;
        }

        return lastSeenAt > lastHeartbeatAt ? lastSeenAt : lastHeartbeatAt;
    }
}
