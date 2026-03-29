// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the severity level of an alert.
/// </summary>
public enum AlertSeverity : short
{
    /// <summary>Informational alert.</summary>
    Info = 1,
    /// <summary>Warning alert.</summary>
    Warning = 2,
    /// <summary>Critical alert.</summary>
    Critical = 3
}
