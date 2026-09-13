// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// One row of the health rule's input matrix together with the status the rule must produce for it.
/// </summary>
/// <param name="CpuUsagePercent">Reported CPU usage, or null when the machine has not reported it.</param>
/// <param name="MemoryUsagePercent">Reported memory usage, or null when the machine has not reported it.</param>
/// <param name="MaxDiskUsagePercent">Highest disk usage across mounts, or null when not reported.</param>
/// <param name="FailedServices">Count of failed systemd units.</param>
/// <param name="HasDiskHealthIssue">Whether SMART reported a failing disk.</param>
/// <param name="HasHardwareIssue">Whether hardware health reported a fault.</param>
/// <param name="LastSeenSecondsAgo">Age of the last server receipt in seconds, or null for a machine that has never been seen.</param>
/// <param name="Expected">Health status the rule must write: 0 Healthy, 1 Warning, 2 Critical, 3 Offline.</param>
/// <param name="Description">Short label naming the boundary this case sits on.</param>
public sealed record HealthRuleCase(
    int? CpuUsagePercent,
    int? MemoryUsagePercent,
    int? MaxDiskUsagePercent,
    int FailedServices,
    bool HasDiskHealthIssue,
    bool HasHardwareIssue,
    int? LastSeenSecondsAgo,
    short Expected,
    string Description)
{
    /// <summary>
    /// Names the case in test output, so a failing row is identifiable without counting arguments.
    /// </summary>
    public override string ToString()
    {
        return Description;
    }
}
