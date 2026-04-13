// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// CPU info telemetry payload (type=3).
/// </summary>
public sealed class CpuInfoPayload
{
    /// <summary>CPU device identifier.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>CPU model name.</summary>
    public string Model { get; set; } = "";

    /// <summary>CPU manufacturer.</summary>
    public string Manufacturer { get; set; } = "";

    /// <summary>Processor type / architecture.</summary>
    public string ProcessorType { get; set; } = "";

    /// <summary>Number of physical cores (string from agent).</summary>
    public string NumberOfCores { get; set; } = "";

    /// <summary>Number of logical processors.</summary>
    public int LogicalProcessors { get; set; }

    /// <summary>Current clock speed in MHz.</summary>
    public int CurrentClockSpeed { get; set; }

    /// <summary>Maximum clock speed in MHz.</summary>
    public int MaxClockSpeed { get; set; }

    /// <summary>CPU socket designation.</summary>
    public string SocketDesignation { get; set; } = "";
}
