// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Telemetry;

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>
/// Reduces a raw telemetry batch to one <see cref="MachineStatePatch"/> per machine by
/// selecting, for each telemetry type, the latest row by (ReceivedAt, Id) and parsing it.
/// LastSeenAt is the max ReceivedAt across all of the machine's rows. Pure: no I/O.
/// </summary>
internal static class MachineStateBatchCollapser
{
    /// <summary>Collapses a batch into per-machine patches plus the rows that failed to parse.</summary>
    /// <param name="batch">The raw telemetry rows to collapse.</param>
    /// <returns>One patch per machine, together with the rows that could not be parsed.</returns>
    internal static CollapseResult Collapse(IReadOnlyList<MachineTelemetry> batch)
    {
        List<MachineStatePatch> patches = [];
        List<SkippedTelemetryRow> skipped = [];

        foreach (IGrouping<long, MachineTelemetry> machineGroup in batch.GroupBy(r => r.MachineId))
        {
            long machineId = machineGroup.Key;
            DateTimeOffset lastSeenAt = machineGroup.Max(r => r.ReceivedAt);

            MachineStatePatch patch = new() { MachineId = machineId, LastSeenAt = lastSeenAt };

            foreach (IGrouping<short, MachineTelemetry> typeGroup in machineGroup.GroupBy(r => r.TelemetryType))
            {
                // Latest row for this type by (ReceivedAt, Id).
                MachineTelemetry winner = typeGroup
                    .OrderBy(r => r.ReceivedAt)
                    .ThenBy(r => r.Id)
                    .Last();

                patch = ApplyWinner(patch, winner, skipped);
            }

            patches.Add(patch);
        }

        return new CollapseResult(patches, skipped);
    }

    private static MachineStatePatch ApplyWinner(MachineStatePatch patch, MachineTelemetry winner, List<SkippedTelemetryRow> skipped)
    {
        switch (winner.TelemetryType)
        {
            case TelemetryTypeIds.SystemInfo:
                if (TelemetryPayloadParser.TryParseSystemInfo(winner.Payload, out SystemInfoFragment? systemInfo))
                {
                    return patch with { SystemInfo = systemInfo };
                }

                break;

            case TelemetryTypeIds.OsVersion:
                if (TelemetryPayloadParser.TryParseOsVersion(winner.Payload, out OsVersionFragment? osVersion))
                {
                    return patch with { OsVersion = osVersion };
                }

                break;

            case TelemetryTypeIds.CpuInfo:
                if (TelemetryPayloadParser.TryParseCpuInfo(winner.Payload, out CpuInfoFragment? cpuInfo))
                {
                    return patch with { CpuInfo = cpuInfo };
                }

                break;

            case TelemetryTypeIds.MemoryInfo:
                if (TelemetryPayloadParser.TryParseMemoryInfo(winner.Payload, out MemoryInfoFragment? memoryInfo))
                {
                    return patch with { MemoryInfo = memoryInfo };
                }

                break;

            case TelemetryTypeIds.DiskInfo:
                if (TelemetryPayloadParser.TryParseDiskInfo(winner.Payload, out DiskInfoFragment? diskInfo))
                {
                    return patch with { DiskInfo = diskInfo };
                }

                break;

            case TelemetryTypeIds.CpuUsage:
                if (TelemetryPayloadParser.TryParseCpuUsage(winner.Payload, out CpuUsageFragment? cpuUsage))
                {
                    return patch with { CpuUsage = cpuUsage };
                }

                break;

            case TelemetryTypeIds.MemoryUsage:
                if (TelemetryPayloadParser.TryParseMemoryUsage(winner.Payload, out MemoryUsageFragment? memoryUsage))
                {
                    return patch with { MemoryUsage = memoryUsage };
                }

                break;

            case TelemetryTypeIds.DiskUsage:
                if (TelemetryPayloadParser.TryParseDiskUsage(winner.Payload, out DiskUsageFragment? diskUsage))
                {
                    return patch with { DiskUsage = diskUsage };
                }

                break;

            case TelemetryTypeIds.SshSessions:
                if (TelemetryPayloadParser.TryParseSshSessions(winner.Payload, out SshSessionsFragment? sshSessions))
                {
                    return patch with { SshSessions = sshSessions };
                }

                break;

            case TelemetryTypeIds.HardwareHealth:
                if (TelemetryPayloadParser.TryParseHardwareHealth(winner.Payload, out HardwareHealthFragment? hardwareHealth))
                {
                    return patch with { HardwareHealth = hardwareHealth };
                }

                break;

            case TelemetryTypeIds.PackageUpdates:
                if (TelemetryPayloadParser.TryParsePackageUpdates(winner.Payload, out PackageUpdatesFragment? packageUpdates))
                {
                    return patch with { PackageUpdates = packageUpdates };
                }

                break;

            case TelemetryTypeIds.ServiceStatus:
                if (TelemetryPayloadParser.TryParseServiceStatus(winner.Payload, out ServiceStatusFragment? serviceStatus))
                {
                    return patch with { ServiceStatus = serviceStatus };
                }

                break;

            default:
                // Unknown type: ignore (matches the old switch's default branch).
                return patch;
        }

        skipped.Add(new SkippedTelemetryRow(winner.MachineId, winner.TelemetryType, winner.Id));

        return patch;
    }
}
