// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Single disk info mount entry.
/// </summary>
public sealed class DiskInfoEntryDto
{
    /// <summary>Device path.</summary>
    [JsonPropertyName("device")]
    public string Device { get; set; } = "";

    /// <summary>Mount point.</summary>
    [JsonPropertyName("mount_point")]
    public string MountPoint { get; set; } = "";

    /// <summary>Filesystem type.</summary>
    [JsonPropertyName("fs_type")]
    public string FsType { get; set; } = "";

    /// <summary>Total bytes.</summary>
    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    /// <summary>Used bytes.</summary>
    [JsonPropertyName("used_bytes")]
    public long UsedBytes { get; set; }

    /// <summary>Available bytes.</summary>
    [JsonPropertyName("available_bytes")]
    public long AvailableBytes { get; set; }

    /// <summary>Percentage used.</summary>
    [JsonPropertyName("percent_used")]
    public double PercentUsed { get; set; }
}
