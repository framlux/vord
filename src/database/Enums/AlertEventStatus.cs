// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the status of an alert event.
/// </summary>
public enum AlertEventStatus : short
{
    /// <summary>Alert has been triggered and is active.</summary>
    Triggered = 1,
    /// <summary>Alert has been acknowledged by a user.</summary>
    Acknowledged = 2,
    /// <summary>Alert condition has been resolved.</summary>
    Resolved = 3
}
