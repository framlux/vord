// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Flips the default value of <c>IntegrationDeliveryAttempts.Status</c> from <c>1</c>
/// (<see cref="Enums.IntegrationDeliveryAttemptStatus.Succeeded"/>) to <c>0</c>
/// (<see cref="Enums.IntegrationDeliveryAttemptStatus.Pending"/>). The original default of
/// 1 was intentional for backfilling legacy rows under the previous record-on-success design;
/// after the claim-then-send-then-mark idempotency design landed (see
/// <see cref="IntegrationDeliveryAttemptStatusMigration"/>), any new row should default to
/// Pending so an accidental insert without an explicit Status value fails closed rather than
/// silently appearing as already-delivered (which would skip the integration entirely on the
/// next attempt).
/// </summary>
/// <remarks>
/// This migration is additive and safe to apply against either fresh databases or databases that
/// already received the original (default=1) form of the schema. Per saved feedback in
/// CLAUDE.md, existing migration files are never modified once shipped — corrective changes
/// always ride in a new migration.
/// </remarks>
[MigrationVersion(2026, 05, 20, 1)]
public sealed class IntegrationDeliveryAttemptStatusDefaultMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        IfDatabase("PostgreSQL").Execute.Sql(
            @"ALTER TABLE ""IntegrationDeliveryAttempts"" ALTER COLUMN ""Status"" SET DEFAULT 0;");

        // SQLite stores the column DEFAULT inside the table definition; the existing default
        // applies only when the column is omitted from INSERT statements. To flip it we would
        // need a full table rebuild (as in the prior status-column migration). SQLite is only
        // used for in-memory unit tests, and those tests insert Status explicitly via the
        // repository, so the default is never relied upon. Skip the SQLite path to keep this
        // migration cheap on the in-memory test path.
    }

    /// <inheritdoc/>
    public override void Down()
    {
        IfDatabase("PostgreSQL").Execute.Sql(
            @"ALTER TABLE ""IntegrationDeliveryAttempts"" ALTER COLUMN ""Status"" SET DEFAULT 1;");
    }
}
