// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Extends IntegrationDeliveryAttempts with the lifecycle columns required by the
/// claim-then-send-then-mark idempotency design:
/// <list type="bullet">
///   <item>Adds <c>Status</c> (int, NOT NULL, default = 1 = Succeeded) so existing rows — which
///         were inserted under the previous "record-on-success" design — are treated as
///         Succeeded. New rows start as Pending (0) before the outbound HTTP POST.</item>
///   <item>Adds <c>AttemptedAt</c> (datetimeoffset, NOT NULL) holding the timestamp the row was
///         first claimed. Backfilled from the existing <c>SucceededAt</c> for legacy rows.</item>
///   <item>Drops the NOT NULL constraint on <c>SucceededAt</c>; the column remains for
///         Succeeded rows but is NULL while a claim is Pending.</item>
/// </list>
/// The unique index on (AlertEventId, IntegrationEndpointId) is unchanged — created by
/// <see cref="HangfireSchemaMigration"/>. It now also enforces single-claim semantics.
/// </summary>
[MigrationVersion(2026, 05, 18, 2)]
public sealed class IntegrationDeliveryAttemptStatusMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        // PostgreSQL path: in-place column additions plus ALTER COLUMN to relax SucceededAt
        // NOT NULL. Backfill AttemptedAt from SucceededAt for any legacy rows.
        IfDatabase("PostgreSQL").Execute.Sql(
            @"ALTER TABLE ""IntegrationDeliveryAttempts"" ADD COLUMN ""Status"" INTEGER NOT NULL DEFAULT 1;");
        IfDatabase("PostgreSQL").Execute.Sql(
            @"ALTER TABLE ""IntegrationDeliveryAttempts"" ADD COLUMN ""AttemptedAt"" TIMESTAMPTZ NULL;");
        IfDatabase("PostgreSQL").Execute.Sql(
            @"UPDATE ""IntegrationDeliveryAttempts"" SET ""AttemptedAt"" = ""SucceededAt"" WHERE ""AttemptedAt"" IS NULL;");
        IfDatabase("PostgreSQL").Execute.Sql(
            @"ALTER TABLE ""IntegrationDeliveryAttempts"" ALTER COLUMN ""AttemptedAt"" SET NOT NULL;");
        IfDatabase("PostgreSQL").Execute.Sql(
            @"ALTER TABLE ""IntegrationDeliveryAttempts"" ALTER COLUMN ""SucceededAt"" DROP NOT NULL;");

        // SQLite path: cannot ALTER COLUMN to drop NOT NULL. Standard idiom is table-rebuild:
        // create the new schema, copy data, drop old, rename. Foreign keys and the unique index
        // must be re-declared. Wrapped in a transaction by FluentMigrator so an in-flight crash
        // doesn't leave the rename half-applied.
        IfDatabase("SQLite").Execute.Sql(@"
            CREATE TABLE ""IntegrationDeliveryAttempts_new"" (
                ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""AlertEventId"" INTEGER NOT NULL,
                ""IntegrationEndpointId"" INTEGER NOT NULL,
                ""Status"" INTEGER NOT NULL DEFAULT 1,
                ""AttemptedAt"" TEXT NOT NULL,
                ""SucceededAt"" TEXT NULL,
                CONSTRAINT ""FK_IntegrationDeliveryAttempts_AlertEvents""
                    FOREIGN KEY (""AlertEventId"") REFERENCES ""AlertEvents"" (""Id"") ON DELETE CASCADE,
                CONSTRAINT ""FK_IntegrationDeliveryAttempts_Integrations""
                    FOREIGN KEY (""IntegrationEndpointId"") REFERENCES ""IntegrationEndpoints"" (""Id"") ON DELETE CASCADE
            );");
        IfDatabase("SQLite").Execute.Sql(@"
            INSERT INTO ""IntegrationDeliveryAttempts_new""
                (""Id"", ""AlertEventId"", ""IntegrationEndpointId"", ""Status"", ""AttemptedAt"", ""SucceededAt"")
            SELECT ""Id"", ""AlertEventId"", ""IntegrationEndpointId"", 1, ""SucceededAt"", ""SucceededAt""
            FROM ""IntegrationDeliveryAttempts"";");
        IfDatabase("SQLite").Execute.Sql(@"DROP TABLE ""IntegrationDeliveryAttempts"";");
        IfDatabase("SQLite").Execute.Sql(@"ALTER TABLE ""IntegrationDeliveryAttempts_new"" RENAME TO ""IntegrationDeliveryAttempts"";");
        IfDatabase("SQLite").Execute.Sql(@"
            CREATE UNIQUE INDEX ""UX_IntegrationDeliveryAttempts_EventIntegration""
            ON ""IntegrationDeliveryAttempts"" (""AlertEventId"", ""IntegrationEndpointId"");");
    }

    /// <inheritdoc/>
    public override void Down()
    {
        // PostgreSQL: restore NOT NULL on SucceededAt (deleting any Pending rows first to avoid
        // violating it) and drop the new columns.
        IfDatabase("PostgreSQL").Execute.Sql(
            @"DELETE FROM ""IntegrationDeliveryAttempts"" WHERE ""SucceededAt"" IS NULL;");
        IfDatabase("PostgreSQL").Execute.Sql(
            @"ALTER TABLE ""IntegrationDeliveryAttempts"" ALTER COLUMN ""SucceededAt"" SET NOT NULL;");
        IfDatabase("PostgreSQL").Execute.Sql(
            @"ALTER TABLE ""IntegrationDeliveryAttempts"" DROP COLUMN ""AttemptedAt"";");
        IfDatabase("PostgreSQL").Execute.Sql(
            @"ALTER TABLE ""IntegrationDeliveryAttempts"" DROP COLUMN ""Status"";");

        // SQLite: reverse table rebuild to restore the original three-column schema.
        IfDatabase("SQLite").Execute.Sql(@"
            CREATE TABLE ""IntegrationDeliveryAttempts_old"" (
                ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ""AlertEventId"" INTEGER NOT NULL,
                ""IntegrationEndpointId"" INTEGER NOT NULL,
                ""SucceededAt"" TEXT NOT NULL,
                CONSTRAINT ""FK_IntegrationDeliveryAttempts_AlertEvents""
                    FOREIGN KEY (""AlertEventId"") REFERENCES ""AlertEvents"" (""Id"") ON DELETE CASCADE,
                CONSTRAINT ""FK_IntegrationDeliveryAttempts_Integrations""
                    FOREIGN KEY (""IntegrationEndpointId"") REFERENCES ""IntegrationEndpoints"" (""Id"") ON DELETE CASCADE
            );");
        IfDatabase("SQLite").Execute.Sql(@"
            INSERT INTO ""IntegrationDeliveryAttempts_old"" (""Id"", ""AlertEventId"", ""IntegrationEndpointId"", ""SucceededAt"")
            SELECT ""Id"", ""AlertEventId"", ""IntegrationEndpointId"", ""SucceededAt""
            FROM ""IntegrationDeliveryAttempts""
            WHERE ""SucceededAt"" IS NOT NULL;");
        IfDatabase("SQLite").Execute.Sql(@"DROP TABLE ""IntegrationDeliveryAttempts"";");
        IfDatabase("SQLite").Execute.Sql(@"ALTER TABLE ""IntegrationDeliveryAttempts_old"" RENAME TO ""IntegrationDeliveryAttempts"";");
        IfDatabase("SQLite").Execute.Sql(@"
            CREATE UNIQUE INDEX ""UX_IntegrationDeliveryAttempts_EventIntegration""
            ON ""IntegrationDeliveryAttempts"" (""AlertEventId"", ""IntegrationEndpointId"");");
    }
}
