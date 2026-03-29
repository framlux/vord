// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;
using System.Text.Json;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Computes machine health status from MachineState metrics.
/// Extracted for testability.
/// </summary>
public static class HealthComputer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Computes the health status for a machine based on its state and online status.
    /// </summary>
    /// <param name="state">The current machine state containing metrics.</param>
    /// <param name="isOnline">Whether the machine is currently online.</param>
    /// <returns>The computed health status.</returns>
    public static MachineHealthStatus Compute(MachineState state, bool isOnline)
    {
        if (isOnline == false)
        {
            return MachineHealthStatus.Offline;
        }

        // Deserialize JSON columns once for both critical and warning checks.
        List<DiskUsageEntryDto>? disks = state.DiskUsages is not null
            ? TryDeserializeJson<List<DiskUsageEntryDto>>(state.DiskUsages)
            : null;

        HardwareHealthPayload? hw = state.HardwareHealth is not null
            ? TryDeserializeJson<HardwareHealthPayload>(state.HardwareHealth)
            : null;

        // Critical checks.
        if ((state.CpuUsagePercent >= 95) || (state.MemoryUsagePercent >= 95))
        {
            return MachineHealthStatus.Critical;
        }

        if (state.FailedServices > 0)
        {
            return MachineHealthStatus.Critical;
        }

        if (disks?.Exists(d => d.UsagePercent >= 95) == true)
        {
            return MachineHealthStatus.Critical;
        }

        if (hw is not null)
        {
            if (hw.DiskSmart.Exists(d => string.Equals(d.HealthStatus, "FAILED", StringComparison.OrdinalIgnoreCase)))
            {
                return MachineHealthStatus.Critical;
            }

            if (hw.Fans.Exists(f => f.Rpm == 0))
            {
                return MachineHealthStatus.Critical;
            }

            if (hw.PowerSupplies.Exists(p => string.Equals(p.Status, "ok", StringComparison.OrdinalIgnoreCase) == false))
            {
                return MachineHealthStatus.Critical;
            }
        }

        // Warning checks.
        if ((state.CpuUsagePercent >= 80) || (state.MemoryUsagePercent >= 80))
        {
            return MachineHealthStatus.Warning;
        }

        if (disks?.Exists(d => d.UsagePercent >= 80) == true)
        {
            return MachineHealthStatus.Warning;
        }

        if (hw is not null)
        {
            if (hw.DiskSmart.Exists(d => (d.WearoutPercent > 80) || (d.TemperatureCelsius >= 55)))
            {
                return MachineHealthStatus.Warning;
            }
        }

        return MachineHealthStatus.Healthy;
    }

    private static T? TryDeserializeJson<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
