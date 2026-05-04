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
            .WithColumn("Tier").AsInt32().NotNullable().Unique()
            .WithColumn("MachineLimit").AsInt32().NotNullable()
            .WithColumn("RetentionDays").AsInt32().NotNullable()
            .WithColumn("AlertRuleLimit").AsInt32().NotNullable()
            .WithColumn("WebhookLimit").AsInt32().NotNullable()
            .WithColumn("UpdatedAt").AsDateTimeOffset().NotNullable();

        Create.Table("TenantSubscriptionOverrides")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("TenantId").AsInt32().NotNullable().Unique()
                .ForeignKey("FK_TenantSubscriptionOverrides_Tenants", TableNames.Tenants, "Id")
            .WithColumn("MachineLimit").AsInt32().Nullable()
            .WithColumn("RetentionDays").AsInt32().Nullable()
            .WithColumn("AlertRuleLimit").AsInt32().Nullable()
            .WithColumn("WebhookLimit").AsInt32().Nullable()
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("UpdatedAt").AsDateTimeOffset().NotNullable();

        // Seed default tier limits matching TierDefaults configuration
        Insert.IntoTable("TierFeatureLimits").Row(new
        {
            Tier = 1, // SubscriptionTier.Free
            MachineLimit = 3,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        Insert.IntoTable("TierFeatureLimits").Row(new
        {
            Tier = 2, // SubscriptionTier.Pro
            MachineLimit = 1000,
            RetentionDays = 30,
            AlertRuleLimit = 10,
            WebhookLimit = 5,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        Insert.IntoTable("TierFeatureLimits").Row(new
        {
            Tier = 3, // SubscriptionTier.Team
            MachineLimit = 10000,
            RetentionDays = 365,
            AlertRuleLimit = 25,
            WebhookLimit = 15,
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
