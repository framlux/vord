// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Removes expiry and usage limit columns from RegistrationTokens since tokens are now valid until manually revoked.
/// </summary>
[MigrationVersion(2026, 04, 02, 1)]
public sealed class RemoveTokenExpiryAndUsageLimits : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Delete.Column("ExpiresAt").FromTable("RegistrationTokens");
        Delete.Column("MaxUses").FromTable("RegistrationTokens");
        Delete.Column("UsedCount").FromTable("RegistrationTokens");
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Alter.Table("RegistrationTokens")
            .AddColumn("ExpiresAt").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .AddColumn("MaxUses").AsInt32().NotNullable().WithDefaultValue(100)
            .AddColumn("UsedCount").AsInt32().NotNullable().WithDefaultValue(0);
    }
}
