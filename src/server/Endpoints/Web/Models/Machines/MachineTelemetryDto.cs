// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;

/// <summary>
/// Telemetry record returned to the UI.
/// </summary>
public sealed class MachineTelemetryDto
{
    /// <summary>
    /// The telemetry record ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The telemetry type identifier.
    /// </summary>
    public short TelemetryType { get; set; }

    /// <summary>
    /// The JSON payload of the telemetry data.
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// When the telemetry was received.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; set; }
}
