// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Guarantees a tenant holds at most one built-in rule per metric. Provisioning is idempotent in
/// application code, but that check cannot be atomic against a concurrently replayed billing
/// webhook — this constraint is what makes a double-seed impossible rather than unlikely. The
/// predicate is required: custom rules may freely share a metric, and several per tenant is the
/// expected shape of the Team tier.
/// </summary>
[MigrationVersion(2026, 08, 28, 1)]
public sealed class AddAlertRulesBuiltInUniqueIndex : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        // FluentMigrator's fluent index builder has no partial-index support, so both dialects are
        // raw SQL. The false/0 split matches the existing partial indexes on the alert tables.
        IfDatabase("PostgreSQL").Execute.Sql(
            """CREATE UNIQUE INDEX "UX_AlertRules_TenantId_Metric_BuiltIn" ON "AlertRules" ("TenantId", "Metric") WHERE "IsCustom" = false""");

        IfDatabase("SQLite").Execute.Sql(
            """CREATE UNIQUE INDEX "UX_AlertRules_TenantId_Metric_BuiltIn" ON "AlertRules" ("TenantId", "Metric") WHERE "IsCustom" = 0""");
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Execute.Sql("""DROP INDEX IF EXISTS "UX_AlertRules_TenantId_Metric_BuiltIn" """);
    }
}
