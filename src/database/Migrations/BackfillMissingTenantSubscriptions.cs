// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Gives every existing tenant a Free, Active subscription row if it does not already have one.
/// </summary>
/// <remarks>
/// <para>
/// Tenants created through the global-admin endpoint were the one creation path that never
/// provisioned a subscription. That was invisible while the mutation gate returned early on a
/// missing row; now that the gate fails closed, such a tenant would be permanently read-only, and
/// no request path or job creates the row — the only writers act on a tenant they have just
/// inserted. The code side is fixed at the creation site, but rows that already exist need this.
/// </para>
/// <para>
/// Free and Active are the same values every other creation path writes, so a backfilled tenant is
/// indistinguishable from one created through onboarding. The row grants no paid entitlement.
/// </para>
/// </remarks>
[MigrationVersion(2026, 08, 25, 1)]
public sealed class BackfillMissingTenantSubscriptions : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        // Tier and Status are stored as the underlying enum integers: SubscriptionTier.Free = 1 and
        // SubscriptionStatus.Active = 1. Free is deliberately not zero — zero is SubscriptionTier.None,
        // which passes the Pro gate whenever the status is Active and would hand every backfilled
        // tenant paid features. Written as raw SQL because the insert is conditional on the absence
        // of a row, which the fluent Insert API cannot express — and scoped to PostgreSQL because
        // the functional test hosts run this same migration set against SQLite, where quoted
        // identifiers and NOW() do not apply. Those hosts create their tenants through the
        // application, which always provisions, so they have nothing to backfill.
        IfDatabase("PostgreSQL").Execute.Sql(
            """
            INSERT INTO "TenantSubscriptions" ("TenantId", "Tier", "Status", "CreatedAt", "UpdatedAt", "CancelAtPeriodEnd")
            SELECT t."Id", 1, 1, NOW(), NOW(), FALSE
            FROM "Tenants" t
            WHERE NOT EXISTS (
                SELECT 1 FROM "TenantSubscriptions" s WHERE s."TenantId" = t."Id"
            );
            """);
    }

    /// <inheritdoc/>
    public override void Down()
    {
        // Deliberately not reversed. The backfilled rows are indistinguishable from ones written by
        // the normal creation paths, so removing them would mean guessing which tenants to strip a
        // subscription from — and a tenant with no row is the broken state this repaired.
    }
}
