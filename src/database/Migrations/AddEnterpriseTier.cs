// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Introduces the Enterprise tier. Its limits row carries Team's values only as a fallback: an
/// Enterprise tenant always has every limit set explicitly by its agreement. Members gain an
/// override column because agreements set a member count, and subscriptions record the agreement
/// revision last applied so an older apply can never overwrite a newer one.
/// </summary>
[MigrationVersion(2026, 10, 04, 1)]
public sealed class AddEnterpriseTier : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        // Tier column: Enterprise = 4. No billable floor: Enterprise is never billed through Stripe.
        Insert.IntoTable("TierFeatureLimits").Row(new
        {
            Tier = 4,
            MachineLimit = 10000,
            RetentionDays = 365,
            AlertRuleLimit = 25,
            WebhookLimit = 15,
            MemberLimit = int.MaxValue,
            MinimumBillableMachines = 0,
            UpdatedAt = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero),
        });

        Alter.Table("TenantSubscriptionOverrides")
            .AddColumn("MemberLimit").AsInt32().Nullable();

        Alter.Table(TableNames.TenantSubscriptions)
            .AddColumn("AppliedAgreementRevision").AsInt32().Nullable();
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Column("AppliedAgreementRevision").FromTable(TableNames.TenantSubscriptions);
        Delete.Column("MemberLimit").FromTable("TenantSubscriptionOverrides");
        Delete.FromTable("TierFeatureLimits").Row(new { Tier = 4 });
    }
}
