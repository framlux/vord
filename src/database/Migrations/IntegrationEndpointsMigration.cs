// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Creates the IntegrationEndpoints table for pre-built and custom webhook integrations,
/// and drops the now-unused WebhookEndpoints table.
/// </summary>
[MigrationVersion(2026, 05, 10, 2)]
public sealed class IntegrationEndpointsMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Delete.Table(TableNames.WebhookEndpoints);

        Create.Table(TableNames.IntegrationEndpoints)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("TenantId").AsInt32().NotNullable()
                .ForeignKey("FK_IntegrationEndpoints_Tenants", TableNames.Tenants, "Id")
            .WithColumn("Provider").AsInt16().NotNullable()
            .WithColumn("Name").AsString(100).NotNullable()
            .WithColumn("Configuration").AsCustom("jsonb").NotNullable()
            .WithColumn("IsEnabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("CreatedByUserId").AsInt32().NotNullable()
                .ForeignKey("FK_IntegrationEndpoints_Users", TableNames.Users, "Id")
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("UpdatedAt").AsDateTimeOffset().Nullable()
            .WithColumn("DeletedAt").AsDateTimeOffset().Nullable()
            .WithColumn("DeletedByUserId").AsInt32().Nullable()
                .ForeignKey("FK_IntegrationEndpoints_DeletedByUsers", TableNames.Users, "Id");

        IfDatabase("PostgreSQL").Execute.Sql("""
            CREATE INDEX "IX_IntegrationEndpoints_TenantId"
            ON "IntegrationEndpoints" ("TenantId")
            WHERE "DeletedAt" IS NULL;
            """);

        IfDatabase("SQLite").Execute.Sql("""
            CREATE INDEX "IX_IntegrationEndpoints_TenantId"
            ON "IntegrationEndpoints" ("TenantId")
            WHERE "DeletedAt" IS NULL;
            """);

        IfDatabase("PostgreSQL").Execute.Sql("""
            CREATE INDEX "IX_IntegrationEndpoints_TenantId_Provider"
            ON "IntegrationEndpoints" ("TenantId", "Provider")
            WHERE "DeletedAt" IS NULL;
            """);

        IfDatabase("SQLite").Execute.Sql("""
            CREATE INDEX "IX_IntegrationEndpoints_TenantId_Provider"
            ON "IntegrationEndpoints" ("TenantId", "Provider")
            WHERE "DeletedAt" IS NULL;
            """);
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Table(TableNames.IntegrationEndpoints);

        Create.Table(TableNames.WebhookEndpoints)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("TenantId").AsInt32().NotNullable()
                .ForeignKey("FK_WebhookEndpoints_Tenants", TableNames.Tenants, "Id")
            .WithColumn("Name").AsString(100).NotNullable()
            .WithColumn("Url").AsString(2048).NotNullable()
            .WithColumn("Secret").AsString(1024).NotNullable()
            .WithColumn("IsEnabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("CreatedByUserId").AsInt32().NotNullable()
                .ForeignKey("FK_WebhookEndpoints_Users", TableNames.Users, "Id")
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable();
    }
}
