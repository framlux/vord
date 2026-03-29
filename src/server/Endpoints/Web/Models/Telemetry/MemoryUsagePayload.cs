// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Memory utilization telemetry payload (type=7).
/// </summary>
public sealed class MemoryUsagePayload
{
    /// <summary>Total memory in bytes.</summary>
    [JsonPropertyName("memory_total")]
    public long MemoryTotal { get; set; }

    /// <summary>Used memory in bytes.</summary>
    [JsonPropertyName("memory_used")]
    public long MemoryUsed { get; set; }

    /// <summary>Memory usage percentage.</summary>
    [JsonPropertyName("memory_usage_percent")]
    public int MemoryUsagePercent { get; set; }
}
