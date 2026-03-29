// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the metric an alert rule evaluates.
/// </summary>
public enum AlertMetric : short
{
    /// <summary>CPU usage percentage.</summary>
    CpuUsage = 1,
    /// <summary>Memory usage percentage.</summary>
    MemoryUsage = 2,
    /// <summary>Disk usage percentage.</summary>
    DiskUsage = 3,
    /// <summary>Machine offline duration.</summary>
    MachineOffline = 4,
    /// <summary>Number of failed systemd services.</summary>
    FailedServices = 5,
    /// <summary>Number of pending security updates.</summary>
    SecurityUpdates = 6,
    /// <summary>Disk SMART health status.</summary>
    DiskHealth = 7
}
