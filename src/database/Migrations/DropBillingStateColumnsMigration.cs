// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Drops the CancelAtPeriodEnd and PendingAction columns from TenantSubscriptions.
/// These billing state fields are now managed exclusively by the billing-api's
/// PendingActions table per the split-authority pattern.
/// </summary>
[MigrationVersion(2026, 05, 03, 3)]
public sealed class DropBillingStateColumnsMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Delete.Column("CancelAtPeriodEnd").FromTable("TenantSubscriptions");
        Delete.Column("PendingAction").FromTable("TenantSubscriptions");
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Alter.Table("TenantSubscriptions")
            .AddColumn("CancelAtPeriodEnd").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("PendingAction").AsInt16().NotNullable().WithDefaultValue(0);
    }
}
