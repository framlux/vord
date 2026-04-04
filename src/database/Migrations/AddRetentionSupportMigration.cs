// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Adds DeletedAt columns and retention-related indexes to AlertEvents, AuditLog,
/// and RemoteCommands tables to support tier-based data retention cleanup.
/// </summary>
[MigrationVersion(2026, 4, 4, 2)]
public sealed class AddRetentionSupportMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Alter.Table(TableNames.AlertEvents)
            .AddColumn("DeletedAt").AsDateTimeOffset().Nullable();

        Alter.Table(TableNames.AuditLog)
            .AddColumn("DeletedAt").AsDateTimeOffset().Nullable();

        Alter.Table(TableNames.RemoteCommands)
            .AddColumn("DeletedAt").AsDateTimeOffset().Nullable();

        Create.Index("IX_AlertEvents_TenantId_DeletedAt")
            .OnTable(TableNames.AlertEvents)
            .OnColumn("TenantId").Ascending()
            .OnColumn("DeletedAt").Ascending();

        Create.Index("IX_AuditLog_TenantId_DeletedAt")
            .OnTable(TableNames.AuditLog)
            .OnColumn("TenantId").Ascending()
            .OnColumn("DeletedAt").Ascending();

        Create.Index("IX_RemoteCommands_TenantId_DeletedAt")
            .OnTable(TableNames.RemoteCommands)
            .OnColumn("TenantId").Ascending()
            .OnColumn("DeletedAt").Ascending();
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Index("IX_AlertEvents_TenantId_DeletedAt").OnTable(TableNames.AlertEvents);
        Delete.Index("IX_AuditLog_TenantId_DeletedAt").OnTable(TableNames.AuditLog);
        Delete.Index("IX_RemoteCommands_TenantId_DeletedAt").OnTable(TableNames.RemoteCommands);

        Delete.Column("DeletedAt").FromTable(TableNames.AlertEvents);
        Delete.Column("DeletedAt").FromTable(TableNames.AuditLog);
        Delete.Column("DeletedAt").FromTable(TableNames.RemoteCommands);
    }
}
