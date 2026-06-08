// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Aligns the SQLite (test) schema with Postgres (production) by removing the
/// <c>FK_AlertEvents_AlertRules</c> constraint that was present only on SQLite.
///
/// Background: <see cref="InitialMigration"/> created <c>AlertEvents</c> with two different
/// schemas — Postgres uses a range-partitioned table where row-level FKs cannot reach across
/// partitions (so the FK was intentionally omitted), while SQLite created a regular table with
/// the FK enforced. That divergence caused the <c>AlertRuleDeleteEndpoint</c>'s
/// resolve-events-then-delete-rule flow to behave differently between test and production:
/// Postgres permits the rule delete to succeed (events are already Resolved by the time the
/// rule row goes away), while SQLite's enforced FK aborts the rule delete because the events
/// still reference the rule. The endpoint relies on the Postgres semantics — see
/// <see cref="HangfireSchemaMigration"/> which similarly removed a Postgres-impossible FK
/// against the partitioned <c>AlertEvents</c> table.
///
/// SQLite cannot drop a constraint in place, so this migration rebuilds the table without the
/// FK, copies all rows, drops the original, renames the replacement, and re-creates the two
/// indexes from the initial schema. Postgres never had the FK, so the migration is a no-op
/// on production.
/// </summary>
[MigrationVersion(2026, 05, 20, 3)]
public sealed class AlertEventsAlertRuleFkRemovalMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        IfDatabase("SQLite").Execute.Sql(@"
            CREATE TABLE ""AlertEvents_new"" (
                ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""AlertRuleId"" INTEGER NOT NULL,
                ""TenantId"" INTEGER NOT NULL,
                ""MachineId"" INTEGER NOT NULL,
                ""Severity"" INTEGER NOT NULL,
                ""Message"" TEXT NOT NULL,
                ""Details"" TEXT NULL,
                ""Status"" INTEGER NOT NULL,
                ""TriggeredAt"" TEXT NOT NULL,
                ""AcknowledgedAt"" TEXT NULL,
                ""AcknowledgedByUserId"" INTEGER NULL,
                ""ResolvedAt"" TEXT NULL,
                CONSTRAINT ""FK_AlertEvents_Tenants""
                    FOREIGN KEY (""TenantId"") REFERENCES ""Tenants"" (""Id"")
            );");
        IfDatabase("SQLite").Execute.Sql(@"
            INSERT INTO ""AlertEvents_new""
                (""Id"", ""AlertRuleId"", ""TenantId"", ""MachineId"", ""Severity"", ""Message"", ""Details"",
                 ""Status"", ""TriggeredAt"", ""AcknowledgedAt"", ""AcknowledgedByUserId"", ""ResolvedAt"")
            SELECT
                ""Id"", ""AlertRuleId"", ""TenantId"", ""MachineId"", ""Severity"", ""Message"", ""Details"",
                ""Status"", ""TriggeredAt"", ""AcknowledgedAt"", ""AcknowledgedByUserId"", ""ResolvedAt""
            FROM ""AlertEvents"";");
        IfDatabase("SQLite").Execute.Sql(@"DROP TABLE ""AlertEvents"";");
        IfDatabase("SQLite").Execute.Sql(@"ALTER TABLE ""AlertEvents_new"" RENAME TO ""AlertEvents"";");
        IfDatabase("SQLite").Execute.Sql(@"
            CREATE INDEX ""IX_AlertEvents_TenantId_TriggeredAt""
            ON ""AlertEvents"" (""TenantId"" ASC, ""TriggeredAt"" DESC);");
        IfDatabase("SQLite").Execute.Sql(@"
            CREATE INDEX ""IX_AlertEvents_RuleId_MachineId_Status""
            ON ""AlertEvents"" (""AlertRuleId"" ASC, ""MachineId"" ASC, ""Status"" ASC);");
    }

    /// <inheritdoc/>
    public override void Down()
    {
        // Down restores the FK by performing the inverse table rebuild. Provided for
        // operational symmetry; in practice SQLite is only used for ephemeral test databases
        // so Down is rarely exercised.
        IfDatabase("SQLite").Execute.Sql(@"
            CREATE TABLE ""AlertEvents_old"" (
                ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""AlertRuleId"" INTEGER NOT NULL,
                ""TenantId"" INTEGER NOT NULL,
                ""MachineId"" INTEGER NOT NULL,
                ""Severity"" INTEGER NOT NULL,
                ""Message"" TEXT NOT NULL,
                ""Details"" TEXT NULL,
                ""Status"" INTEGER NOT NULL,
                ""TriggeredAt"" TEXT NOT NULL,
                ""AcknowledgedAt"" TEXT NULL,
                ""AcknowledgedByUserId"" INTEGER NULL,
                ""ResolvedAt"" TEXT NULL,
                CONSTRAINT ""FK_AlertEvents_AlertRules""
                    FOREIGN KEY (""AlertRuleId"") REFERENCES ""AlertRules"" (""Id""),
                CONSTRAINT ""FK_AlertEvents_Tenants""
                    FOREIGN KEY (""TenantId"") REFERENCES ""Tenants"" (""Id"")
            );");
        IfDatabase("SQLite").Execute.Sql(@"
            INSERT INTO ""AlertEvents_old""
                (""Id"", ""AlertRuleId"", ""TenantId"", ""MachineId"", ""Severity"", ""Message"", ""Details"",
                 ""Status"", ""TriggeredAt"", ""AcknowledgedAt"", ""AcknowledgedByUserId"", ""ResolvedAt"")
            SELECT
                ""Id"", ""AlertRuleId"", ""TenantId"", ""MachineId"", ""Severity"", ""Message"", ""Details"",
                ""Status"", ""TriggeredAt"", ""AcknowledgedAt"", ""AcknowledgedByUserId"", ""ResolvedAt""
            FROM ""AlertEvents"";");
        IfDatabase("SQLite").Execute.Sql(@"DROP TABLE ""AlertEvents"";");
        IfDatabase("SQLite").Execute.Sql(@"ALTER TABLE ""AlertEvents_old"" RENAME TO ""AlertEvents"";");
        IfDatabase("SQLite").Execute.Sql(@"
            CREATE INDEX ""IX_AlertEvents_TenantId_TriggeredAt""
            ON ""AlertEvents"" (""TenantId"" ASC, ""TriggeredAt"" DESC);");
        IfDatabase("SQLite").Execute.Sql(@"
            CREATE INDEX ""IX_AlertEvents_RuleId_MachineId_Status""
            ON ""AlertEvents"" (""AlertRuleId"" ASC, ""MachineId"" ASC, ""Status"" ASC);");
    }
}
