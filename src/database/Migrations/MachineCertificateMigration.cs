// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Removes the unused MachineCertificates table and creates the MachineAuthorizedKeys table
/// for tracking per-machine signing key authorizations.
/// </summary>
[MigrationVersion(2026, 04, 17, 1)]
public sealed class MachineCertificateMigration : Migration
{
    /// <summary>
    /// Drops the MachineCertificates table and creates the MachineAuthorizedKeys table.
    /// </summary>
    public override void Up()
    {
        Delete.Table("MachineCertificates");

        Create.Table(TableNames.MachineAuthorizedKeys)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity().NotNullable()
            .WithColumn("MachineId").AsInt64().NotNullable().ForeignKey(TableNames.Machines, "Id").Indexed()
            .WithColumn("SigningKeyId").AsInt32().NotNullable().ForeignKey(TableNames.UserSigningKeys, "Id").Indexed()
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id").Indexed()
            .WithColumn("AuthorizedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("AuthorizedByUserId").AsInt32().NotNullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("RevokedAt").AsDateTimeOffset().Nullable()
            .WithColumn("RevokedByUserId").AsInt32().Nullable().ForeignKey(TableNames.Users, "Id");

        Create.Index("IX_MachineAuthorizedKeys_MachineId_SigningKeyId")
            .OnTable(TableNames.MachineAuthorizedKeys)
            .OnColumn("MachineId").Ascending()
            .OnColumn("SigningKeyId").Ascending()
            .WithOptions().Unique();
    }

    /// <summary>
    /// Drops the MachineAuthorizedKeys table and recreates the MachineCertificates table.
    /// </summary>
    public override void Down()
    {
        Delete.Table(TableNames.MachineAuthorizedKeys);

        Create.Table("MachineCertificates")
            .WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
            .WithColumn("MachineId").AsInt64().NotNullable().ForeignKey(TableNames.Machines, "Id").Indexed()
            .WithColumn("Thumbprint").AsString(128).NotNullable().Unique()
            .WithColumn("IssuedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("ExpiresAt").AsDateTimeOffset().NotNullable()
            .WithColumn("RevokedAt").AsDateTimeOffset().Nullable();
    }
}
