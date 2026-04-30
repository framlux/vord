// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Adds configurable per-tenant limits for alert rules and webhook endpoints
/// to the TenantSubscriptions table.
/// </summary>
[MigrationVersion(2026, 04, 29, 1)]
public sealed class AlertSubscriptionLimitsMigration : Migration
{
    /// <summary>
    /// Adds AlertRuleLimit and WebhookLimit nullable integer columns.
    /// Null means unlimited; 0 means blocked (Free tier).
    /// </summary>
    public override void Up()
    {
        Alter.Table(TableNames.TenantSubscriptions)
            .AddColumn("AlertRuleLimit").AsInt32().Nullable()
            .AddColumn("WebhookLimit").AsInt32().Nullable();
    }

    /// <summary>
    /// Removes AlertRuleLimit and WebhookLimit columns.
    /// </summary>
    public override void Down()
    {
        Delete.Column("AlertRuleLimit").FromTable(TableNames.TenantSubscriptions);
        Delete.Column("WebhookLimit").FromTable(TableNames.TenantSubscriptions);
    }
}
