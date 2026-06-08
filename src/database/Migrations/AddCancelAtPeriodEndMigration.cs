// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Re-adds the CancelAtPeriodEnd column to TenantSubscriptions. The column was previously
/// dropped under the split-authority pattern (see DropBillingStateColumnsMigration), but the
/// StripeSyncJob now needs to mirror Stripe's cancel-at-period-end flag locally so the UI and
/// renewal-prompt flow see the state immediately rather than waiting for the subscription to
/// transition to "canceled".
/// </summary>
[MigrationVersion(2026, 05, 18, 3)]
public sealed class AddCancelAtPeriodEndMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Alter.Table(TableNames.TenantSubscriptions)
            .AddColumn("CancelAtPeriodEnd").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Column("CancelAtPeriodEnd").FromTable(TableNames.TenantSubscriptions);
    }
}
