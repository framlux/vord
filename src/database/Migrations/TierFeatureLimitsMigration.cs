// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Creates the TierFeatureLimits and TenantSubscriptionOverrides tables.
/// TierFeatureLimits replaces the hardcoded SubscriptionOptions configuration
/// with a database-managed table for tier-based feature limits.
/// TenantSubscriptionOverrides allows per-customer limit overrides.
/// </summary>
[MigrationVersion(2026, 05, 03, 1)]
public sealed class TierFeatureLimitsMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Create.Table("TierFeatureLimits")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Tier").AsInt16().NotNullable().Unique()
            .WithColumn("MachineLimit").AsInt32().Nullable()
            .WithColumn("RetentionDays").AsInt32().NotNullable()
            .WithColumn("AlertRuleLimit").AsInt32().Nullable()
            .WithColumn("WebhookLimit").AsInt32().Nullable()
            .WithColumn("UpdatedAt").AsDateTimeOffset().NotNullable();

        Create.Table("TenantSubscriptionOverrides")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("TenantId").AsInt32().NotNullable().Unique()
            .WithColumn("MachineLimit").AsInt32().Nullable()
            .WithColumn("RetentionDays").AsInt32().Nullable()
            .WithColumn("AlertRuleLimit").AsInt32().Nullable()
            .WithColumn("WebhookLimit").AsInt32().Nullable()
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("UpdatedAt").AsDateTimeOffset().NotNullable();

        // Seed with current hardcoded values from SubscriptionOptions
        // Free tier: MachineLimit=3, RetentionDays=1, AlertRuleLimit=0, WebhookLimit=0
        Insert.IntoTable("TierFeatureLimits").Row(new
        {
            Tier = 1, // SubscriptionTier.Free
            MachineLimit = 3,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Pro tier: MachineLimit=null (unlimited), RetentionDays=30, AlertRuleLimit=25, WebhookLimit=5
        Insert.IntoTable("TierFeatureLimits").Row(new
        {
            Tier = 2, // SubscriptionTier.Pro
            RetentionDays = 30,
            AlertRuleLimit = 25,
            WebhookLimit = 5,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Team tier: MachineLimit=null (unlimited), RetentionDays=365, AlertRuleLimit=100, WebhookLimit=25
        Insert.IntoTable("TierFeatureLimits").Row(new
        {
            Tier = 3, // SubscriptionTier.Team
            RetentionDays = 365,
            AlertRuleLimit = 100,
            WebhookLimit = 25,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Table("TenantSubscriptionOverrides");
        Delete.Table("TierFeatureLimits");
    }
}
