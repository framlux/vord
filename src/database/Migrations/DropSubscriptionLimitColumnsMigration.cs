// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Drops the MachineLimit, RetentionDays, AlertRuleLimit, and WebhookLimit columns
/// from TenantSubscriptions. These limits are now managed by the TierFeatureLimits
/// and TenantSubscriptionOverrides tables.
/// </summary>
[MigrationVersion(2026, 05, 03, 2)]
public sealed class DropSubscriptionLimitColumnsMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Delete.Column("MachineLimit").FromTable("TenantSubscriptions");
        Delete.Column("RetentionDays").FromTable("TenantSubscriptions");
        Delete.Column("AlertRuleLimit").FromTable("TenantSubscriptions");
        Delete.Column("WebhookLimit").FromTable("TenantSubscriptions");
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Alter.Table("TenantSubscriptions")
            .AddColumn("MachineLimit").AsInt32().Nullable()
            .AddColumn("RetentionDays").AsInt32().NotNullable().WithDefaultValue(1)
            .AddColumn("AlertRuleLimit").AsInt32().Nullable()
            .AddColumn("WebhookLimit").AsInt32().Nullable();
    }
}
