// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Memory info telemetry payload (type=4).
/// </summary>
public sealed class MemoryInfoPayload
{
    /// <summary>Total memory in bytes.</summary>
    [JsonPropertyName("memory_total")]
    public long MemoryTotal { get; set; }

    /// <summary>Free memory in bytes.</summary>
    [JsonPropertyName("memory_free")]
    public long MemoryFree { get; set; }

    /// <summary>Available memory in bytes.</summary>
    [JsonPropertyName("memory_available")]
    public long MemoryAvailable { get; set; }

    /// <summary>Total swap in bytes.</summary>
    [JsonPropertyName("swap_total")]
    public long SwapTotal { get; set; }

    /// <summary>Free swap in bytes.</summary>
    [JsonPropertyName("swap_free")]
    public long SwapFree { get; set; }
}
