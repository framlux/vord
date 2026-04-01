// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Seeds default values for all server configuration settings so the admin panel
/// displays configuration even before any manual edits have been made.
/// </summary>
[MigrationVersion(2026, 03, 30, 1)]
public sealed class SeedDefaultServerConfiguration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Insert.IntoTable("ConfigurationSettings")
            .Row(new { Key = 1, Value = "300", Version = 1 })
            .Row(new { Key = 2, Value = "900", Version = 1 })
            .Row(new { Key = 3, Value = "300", Version = 1 })
            .Row(new { Key = 4, Value = "30", Version = 1 })
            .Row(new { Key = 5, Value = "7", Version = 1 })
            .Row(new { Key = 6, Value = "300", Version = 1 })
            .Row(new { Key = 7, Value = "30", Version = 1 })
            .Row(new { Key = 8, Value = "true", Version = 1 })
            .Row(new { Key = 9, Value = "30", Version = 1 })
            .Row(new { Key = 10, Value = "900", Version = 1 })
            .Row(new { Key = 11, Value = "15", Version = 1 })
            .Row(new { Key = 12, Value = "300", Version = 1 });
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.FromTable("ConfigurationSettings").Row(new { Key = 1 });
        Delete.FromTable("ConfigurationSettings").Row(new { Key = 2 });
        Delete.FromTable("ConfigurationSettings").Row(new { Key = 3 });
        Delete.FromTable("ConfigurationSettings").Row(new { Key = 4 });
        Delete.FromTable("ConfigurationSettings").Row(new { Key = 5 });
        Delete.FromTable("ConfigurationSettings").Row(new { Key = 6 });
        Delete.FromTable("ConfigurationSettings").Row(new { Key = 7 });
        Delete.FromTable("ConfigurationSettings").Row(new { Key = 8 });
        Delete.FromTable("ConfigurationSettings").Row(new { Key = 9 });
        Delete.FromTable("ConfigurationSettings").Row(new { Key = 10 });
        Delete.FromTable("ConfigurationSettings").Row(new { Key = 11 });
        Delete.FromTable("ConfigurationSettings").Row(new { Key = 12 });
    }
}
