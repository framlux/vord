// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Removes Stripe-specific columns from TenantSubscriptions now that billing is handled by a separate service.
/// </summary>
[MigrationVersion(2026, 03, 12, 1)]
public sealed class RemoveStripeColumnsFromSubscriptions : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Delete.Index("IX_TenantSubscriptions_StripeCustomerId").OnTable("TenantSubscriptions");
        Delete.Index("IX_TenantSubscriptions_StripeSubscriptionId").OnTable("TenantSubscriptions");
        Delete.Column("StripeCustomerId").FromTable("TenantSubscriptions");
        Delete.Column("StripeSubscriptionId").FromTable("TenantSubscriptions");
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Alter.Table("TenantSubscriptions")
            .AddColumn("StripeCustomerId").AsString(255).Nullable().Indexed()
            .AddColumn("StripeSubscriptionId").AsString(255).Nullable().Indexed();
    }
}
