// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Single disk mount usage entry.
/// </summary>
public sealed class DiskUsageEntryDto
{
    /// <summary>Device path.</summary>
    [JsonPropertyName("device")]
    public string Device { get; set; } = "";

    /// <summary>Mount path.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>Block size in bytes.</summary>
    [JsonPropertyName("blocks_size")]
    public long BlocksSize { get; set; }

    /// <summary>Total blocks.</summary>
    [JsonPropertyName("blocks")]
    public long Blocks { get; set; }

    /// <summary>Free blocks.</summary>
    [JsonPropertyName("blocks_free")]
    public long BlocksFree { get; set; }

    /// <summary>Available blocks.</summary>
    [JsonPropertyName("blocks_available")]
    public long BlocksAvailable { get; set; }

    /// <summary>Used blocks.</summary>
    [JsonPropertyName("blocks_used")]
    public long BlocksUsed { get; set; }

    /// <summary>Usage percentage.</summary>
    [JsonPropertyName("usage_percent")]
    public int UsagePercent { get; set; }
}
