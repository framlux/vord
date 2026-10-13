// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>
/// The lengths of the bounded string columns of MachineStateSummary and MachineStateDetail that the
/// projection writes from agent-reported text, and the cut that keeps a value inside them.
/// </summary>
/// <remarks>
/// Postgres rejects a value longer than its <c>varchar(n)</c> column and the projection retries a
/// failing batch for ever, so one over-long report would freeze the state of every machine in its
/// shard. SQLite ignores lengths, so only a Postgres test can catch a limit that has drifted from
/// the schema; the integration tests compare these constants with the live columns.
/// </remarks>
internal static class MachineStateColumnLimits
{
    /// <summary>MachineStateSummary.Hostname.</summary>
    internal const int Hostname = 255;

    /// <summary>MachineStateSummary.HardwareModel.</summary>
    internal const int HardwareModel = 255;

    /// <summary>MachineStateSummary.OsName.</summary>
    internal const int OsName = 255;

    /// <summary>MachineStateSummary.OsVersion.</summary>
    internal const int OsVersion = 64;

    /// <summary>MachineStateDetail.HardwareVendor.</summary>
    internal const int HardwareVendor = 255;

    /// <summary>MachineStateDetail.HardwareSerial.</summary>
    internal const int HardwareSerial = 255;

    /// <summary>MachineStateDetail.CpuBrand.</summary>
    internal const int CpuBrand = 255;

    /// <summary>MachineStateDetail.BiosVersion.</summary>
    internal const int BiosVersion = 64;

    /// <summary>MachineStateDetail.Kernel.</summary>
    internal const int Kernel = 255;

    /// <summary>MachineStateDetail.CpuType.</summary>
    internal const int CpuType = 64;

    /// <summary>MachineStateDetail.AgentVersion.</summary>
    internal const int AgentVersion = 64;

    /// <summary>
    /// Cuts a value to at most <paramref name="maxLength"/> UTF-16 characters. Postgres counts
    /// characters, never more than the UTF-16 units, so a value that fits in units always fits the
    /// column. A surrogate pair that would straddle the cut is dropped whole, because its lone
    /// high half cannot be encoded.
    /// </summary>
    /// <param name="value">The reported text, or null when the report carried none.</param>
    /// <param name="maxLength">The most characters the column holds.</param>
    /// <returns>The value unchanged when it fits, otherwise its leading part; null stays null.</returns>
    internal static string? Clamp(string? value, int maxLength)
    {
        if ((value is null) || (value.Length <= maxLength))
        {
            return value;
        }

        int length = maxLength;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }
}
