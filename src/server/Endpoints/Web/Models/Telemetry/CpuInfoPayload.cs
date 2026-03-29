// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// CPU info telemetry payload (type=3).
/// </summary>
public sealed class CpuInfoPayload
{
    /// <summary>CPU device identifier.</summary>
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    /// <summary>CPU model name.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    /// <summary>CPU manufacturer.</summary>
    [JsonPropertyName("manufacturer")]
    public string Manufacturer { get; set; } = "";

    /// <summary>Processor type / architecture.</summary>
    [JsonPropertyName("processor_type")]
    public string ProcessorType { get; set; } = "";

    /// <summary>Number of physical cores (string from agent).</summary>
    [JsonPropertyName("number_of_cores")]
    public string NumberOfCores { get; set; } = "";

    /// <summary>Number of logical processors.</summary>
    [JsonPropertyName("logical_processors")]
    public int LogicalProcessors { get; set; }

    /// <summary>Current clock speed in MHz.</summary>
    [JsonPropertyName("current_clock_speed")]
    public int CurrentClockSpeed { get; set; }

    /// <summary>Maximum clock speed in MHz.</summary>
    [JsonPropertyName("max_clock_speed")]
    public int MaxClockSpeed { get; set; }

    /// <summary>CPU socket designation.</summary>
    [JsonPropertyName("socket_designation")]
    public string SocketDesignation { get; set; } = "";
}
