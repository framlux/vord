// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the comparison operator for an alert rule threshold.
/// </summary>
public enum AlertOperator : short
{
    /// <summary>Value is greater than threshold.</summary>
    GreaterThan = 1,
    /// <summary>Value is less than threshold.</summary>
    LessThan = 2,
    /// <summary>Value equals threshold.</summary>
    EqualTo = 3
}
