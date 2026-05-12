// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the various operating systems.
/// </summary>
public enum OperatingSystems : byte
{
    /// <summary>
    /// Represents an unknown operating system.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Represents the Windows operating system.
    /// </summary>
    Windows = 1,

    /// <summary>
    /// Represents the macOS operating system.
    /// </summary>
    MacOS = 2,

    /// <summary>
    /// Represents the Ubuntu operating system.
    /// </summary>
    Ubuntu = 3,

    /// <summary>
    /// Represents the Fedora operating system.
    /// </summary>
    Fedora = 4,

    /// <summary>
    /// Represents the Red Hat operating system.
    /// </summary>
    RedHat = 5,

    /// <summary>
    /// Represents the Debian operating system.
    /// </summary>
    Debian = 6,
}
