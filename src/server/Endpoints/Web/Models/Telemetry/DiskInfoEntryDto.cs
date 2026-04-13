// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Single disk info mount entry.
/// </summary>
public sealed class DiskInfoEntryDto
{
    /// <summary>Device path.</summary>
    public string Device { get; set; } = "";

    /// <summary>Mount point.</summary>
    public string MountPoint { get; set; } = "";

    /// <summary>Filesystem type.</summary>
    public string FsType { get; set; } = "";

    /// <summary>Total bytes.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Used bytes.</summary>
    public long UsedBytes { get; set; }

    /// <summary>Available bytes.</summary>
    public long AvailableBytes { get; set; }

    /// <summary>Percentage used.</summary>
    public double PercentUsed { get; set; }
}
