// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;
using System.Data;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Adds the Kubernetes inventory: the clusters a tenant's machines are grouped into, the nodes
/// inside each cluster and the cursor the inventory projection resumes from. On PostgreSQL it also
/// adds a partial index over the Kubernetes telemetry types so the projection can read them in id
/// order without scanning the rest of the telemetry table.
/// </summary>
[MigrationVersion(2026, 10, 11, 1)]
public sealed class AddKubernetesInventory : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        // The fingerprint index is deliberately not unique: two clusters in one tenant can be signed
        // by the same CA, and the projection has to be able to store both.
        Create.Table(TableNames.KubernetesClusters)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
            .WithColumn("TenantId").AsInt32().NotNullable()
                .ForeignKey("FK_KubernetesClusters_Tenants", TableNames.Tenants, "Id")
            .WithColumn("CaFingerprint").AsString(64).NotNullable()
            .WithColumn("DisplayName").AsString(100).Nullable()
            .WithColumn("Distro").AsString(16).NotNullable()
            .WithColumn("FirstSeenAt").AsDateTimeOffset().NotNullable()
            .WithColumn("LastSeenAt").AsDateTimeOffset().NotNullable();

        Create.Index("IX_KubernetesClusters_TenantId_CaFingerprint")
            .OnTable(TableNames.KubernetesClusters)
            .OnColumn("TenantId").Ascending()
            .OnColumn("CaFingerprint").Ascending();

        // Nodes follow their cluster: removing a cluster removes its nodes. A node's machine link is
        // a plain reference, so a machine cannot be removed while a node still points at it.
        Create.Table(TableNames.KubernetesNodes)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
            .WithColumn("TenantId").AsInt32().NotNullable()
                .ForeignKey("FK_KubernetesNodes_Tenants", TableNames.Tenants, "Id")
            .WithColumn("ClusterId").AsInt64().NotNullable()
                .ForeignKey("FK_KubernetesNodes_KubernetesClusters", TableNames.KubernetesClusters, "Id")
                    .OnDelete(Rule.Cascade)
            .WithColumn("NodeName").AsString(253).NotNullable()
            .WithColumn("MachineId").AsInt64().Nullable()
                .ForeignKey("FK_KubernetesNodes_Machines", TableNames.Machines, "Id")
            .WithColumn("ReportedCaFingerprint").AsString(64).Nullable()
            .WithColumn("FingerprintMismatch").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("LocalControlPlane").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("KubeletVersion").AsString(64).Nullable()
            .WithColumn("LastLocalReportAt").AsDateTimeOffset().Nullable();

        Create.Index("IX_KubernetesNodes_ClusterId_NodeName")
            .OnTable(TableNames.KubernetesNodes)
            .OnColumn("ClusterId").Ascending()
            .OnColumn("NodeName").Ascending()
            .WithOptions().Unique();

        Create.Index("IX_KubernetesNodes_TenantId")
            .OnTable(TableNames.KubernetesNodes)
            .OnColumn("TenantId").Ascending();

        // A machine is at most one node. Nodes that no enrolled machine has reported carry no machine
        // link, and any number of them may exist, so the uniqueness covers linked nodes only.
        // FluentMigrator's fluent index builder has no partial-index support, so both dialects are
        // raw SQL.
        IfDatabase("PostgreSQL").Execute.Sql("""
            CREATE UNIQUE INDEX "IX_KubernetesNodes_MachineId"
            ON "KubernetesNodes" ("MachineId") WHERE "MachineId" IS NOT NULL
            """);

        IfDatabase("SQLite").Execute.Sql("""
            CREATE UNIQUE INDEX "IX_KubernetesNodes_MachineId"
            ON "KubernetesNodes" ("MachineId") WHERE "MachineId" IS NOT NULL
            """);

        // The projection is a single stream, so the table holds one row whose key is always 1.
        Create.Table(TableNames.KubernetesInventoryProjectionCursor)
            .WithColumn("Id").AsInt32().PrimaryKey().NotNullable()
            .WithColumn("Position").AsInt64().NotNullable()
            .WithColumn("UpdatedAt").AsDateTimeOffset().NotNullable();

        // The projection reads Kubernetes telemetry (types 14 and 15) in id order, and without this
        // index that read would walk every telemetry row. The predicate keeps all other types out of
        // the index. Created on the partitioned parent, Postgres builds it on every existing partition
        // and gives each new daily partition its own copy. It is a plain CREATE INDEX, which blocks
        // inserts into the telemetry table while it builds; the table is small enough at this point
        // for that to be acceptable, and building per partition concurrently would need a migration
        // that runs outside a transaction, which this repository does not support. SQLite is not
        // partitioned and has no projection worker, so it gets no equivalent.
        IfDatabase("PostgreSQL").Execute.Sql("""
            CREATE INDEX IF NOT EXISTS "IX_MachineTelemetry_Kubernetes_Id"
            ON "MachineTelemetry" ("Id") WHERE "TelemetryType" IN (14, 15)
            """);
    }

    /// <inheritdoc/>
    public override void Down()
    {
        IfDatabase("PostgreSQL").Execute.Sql("""DROP INDEX IF EXISTS "IX_MachineTelemetry_Kubernetes_Id" """);

        Delete.Table(TableNames.KubernetesInventoryProjectionCursor);
        Delete.Table(TableNames.KubernetesNodes);
        Delete.Table(TableNames.KubernetesClusters);
    }
}
