// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Enforces a specific migration versioning scheme.
/// </summary>
public sealed class MigrationVersion : MigrationAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationVersion"/> class.
    /// </summary>
    /// <param name="year">The year the migration was made</param>
    /// <param name="month">The month the migration was made</param>
    /// <param name="day">The day the migration was made</param>
    /// <param name="suffix">The suffix for the migration version</param>
    public MigrationVersion(short year, byte month, byte day, byte suffix)
        : base((year * 1000000000L) + (month * 100000L) + (day * 1000L) + suffix)
    {
    }
}
