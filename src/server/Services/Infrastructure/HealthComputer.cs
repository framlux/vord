// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Computes machine health status from MachineStateSummary pre-computed scalars.
/// Extracted for testability.
/// </summary>
public static class HealthComputer
{

    /// <summary>
    /// Computes the health status for a machine based on its summary state and online status.
    /// Uses pre-computed scalar fields (MaxDiskUsagePercent, HasDiskHealthIssue, HasHardwareIssue)
    /// instead of parsing JSONB columns.
    /// </summary>
    /// <param name="state">The current machine state summary containing pre-computed metrics.</param>
    /// <param name="isOnline">Whether the machine is currently online.</param>
    /// <returns>The computed health status.</returns>
    public static MachineHealthStatus Compute(MachineStateSummary state, bool isOnline)
    {
        if (isOnline == false)
        {
            return MachineHealthStatus.Offline;
        }

        // Critical checks using pre-computed scalar fields.
        if ((state.CpuUsagePercent >= 95) || (state.MemoryUsagePercent >= 95))
        {
            return MachineHealthStatus.Critical;
        }

        if (state.FailedServices > 0)
        {
            return MachineHealthStatus.Critical;
        }

        if (state.MaxDiskUsagePercent >= 95)
        {
            return MachineHealthStatus.Critical;
        }

        if (state.HasDiskHealthIssue == true)
        {
            return MachineHealthStatus.Critical;
        }

        if (state.HasHardwareIssue == true)
        {
            return MachineHealthStatus.Critical;
        }

        // Warning checks.
        if ((state.CpuUsagePercent >= 80) || (state.MemoryUsagePercent >= 80))
        {
            return MachineHealthStatus.Warning;
        }

        if (state.MaxDiskUsagePercent >= 80)
        {
            return MachineHealthStatus.Warning;
        }

        return MachineHealthStatus.Healthy;
    }
}
