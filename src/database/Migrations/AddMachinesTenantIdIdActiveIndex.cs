// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Indexes a tenant's registered machines by id. The quota cutoff lookup orders one tenant's
/// non-deleted machines by id and offsets into them to find the last machine inside the limit;
/// the predicate keeps removed machines out of the index, matching what the lookup counts.
/// </summary>
[MigrationVersion(2026, 10, 07, 1)]
public sealed class AddMachinesTenantIdIdActiveIndex : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        // The existing TenantId-only partial index cannot serve an ordering by Id, so Postgres walks
        // the primary key across other tenants' rows to find one tenant's offset. FluentMigrator's
        // fluent index builder has no partial-index support, so both dialects are raw SQL, split
        // on false versus 0.
        IfDatabase("PostgreSQL").Execute.Sql("""
            CREATE INDEX IF NOT EXISTS "IX_Machines_TenantId_Id_Active"
            ON "Machines" ("TenantId", "Id") WHERE "IsDeleted" = false
            """);

        IfDatabase("SQLite").Execute.Sql("""
            CREATE INDEX IF NOT EXISTS "IX_Machines_TenantId_Id_Active"
            ON "Machines" ("TenantId", "Id") WHERE "IsDeleted" = 0
            """);
    }

    /// <inheritdoc/>
    public override void Down()
    {
        IfDatabase("PostgreSQL").Execute.Sql("""DROP INDEX IF EXISTS "IX_Machines_TenantId_Id_Active" """);

        IfDatabase("SQLite").Execute.Sql("""DROP INDEX IF EXISTS "IX_Machines_TenantId_Id_Active" """);
    }
}
