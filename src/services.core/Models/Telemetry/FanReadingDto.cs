// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// Fan sensor reading.
/// </summary>
public sealed class FanReadingDto
{
    /// <summary>Fan name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Fan speed in RPM.</summary>
    public int Rpm { get; set; }

    /// <summary>Sensor status.</summary>
    public string Status { get; set; } = "";
}
