// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Adds the per-tier minimum billable machine count. Licensed Stripe pricing bills the
/// subscription item quantity, so without a floor a paid tenant with no machines registered
/// would pay nothing while retaining paid features.
/// </summary>
[MigrationVersion(2026, 08, 08, 1)]
public sealed class AddMinimumBillableMachines : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Alter.Table("TierFeatureLimits")
            .AddColumn("MinimumBillableMachines").AsInt32().NotNullable().WithDefaultValue(0);

        // Tier column: Free=1, Pro=2, Team=3. Free keeps the default of 0 — it has no
        // Stripe subscription, so there is nothing to floor.
        Update.Table("TierFeatureLimits").Set(new { MinimumBillableMachines = 1 }).Where(new { Tier = 2 });
        Update.Table("TierFeatureLimits").Set(new { MinimumBillableMachines = 3 }).Where(new { Tier = 3 });
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Column("MinimumBillableMachines").FromTable("TierFeatureLimits");
    }
}
