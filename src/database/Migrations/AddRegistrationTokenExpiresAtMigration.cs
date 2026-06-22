// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Adds an ExpiresAt column to RegistrationTokens so registration tokens expire after a
/// configurable lifetime rather than living forever until explicitly revoked. Existing rows
/// are backfilled with a far-future expiry so previously issued tokens continue to work; new
/// tokens get a bounded lifetime set at creation time.
/// </summary>
[MigrationVersion(2026, 06, 15, 1)]
public sealed class AddRegistrationTokenExpiresAtMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        // NOT NULL with a far-future default so the column can be added to a populated table
        // without invalidating tokens that were created before expiry existed.
        Alter.Table(TableNames.RegistrationTokens)
            .AddColumn("ExpiresAt").AsDateTimeOffset().NotNullable()
            .WithDefaultValue(new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero));
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Column("ExpiresAt").FromTable(TableNames.RegistrationTokens);
    }
}
