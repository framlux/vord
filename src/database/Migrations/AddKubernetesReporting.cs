// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;
using System.Data;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Adds what a cluster reporter's snapshot is stored in: the cluster identity and fencing columns on
/// <c>KubernetesClusters</c>, the node detail columns on <c>KubernetesNodes</c>, and the table of
/// machines that can report for each cluster. Roles and conditions are JSON documents kept as text,
/// so the same schema works on PostgreSQL and SQLite.
/// </summary>
[MigrationVersion(2026, 10, 12, 1)]
public sealed class AddKubernetesReporting : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Alter.Table(TableNames.KubernetesClusters)
            .AddColumn("KubeSystemUid").AsString(64).Nullable()
            .AddColumn("ClusterVersion").AsString(64).Nullable()
            .AddColumn("ReporterError").AsString(32).Nullable()
            .AddColumn("RoleOutdated").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("Truncated").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("LeaseTransitions").AsInt64().NotNullable().WithDefaultValue(0)
            .AddColumn("LastSnapshotReceivedAt").AsDateTimeOffset().Nullable()
            .AddColumn("LastSnapshotTelemetryId").AsInt64().Nullable()
            .AddColumn("LeaseUid").AsString(64).Nullable()
            .AddColumn("LeaseCreatedAt").AsDateTimeOffset().Nullable();

        // SQLite cannot add a foreign key constraint to an existing table; a foreign key can only
        // come with the column itself, through ADD COLUMN ... REFERENCES. The two dialects therefore
        // diverge here.
        IfDatabase("PostgreSQL").Alter.Table(TableNames.KubernetesClusters)
            .AddColumn("ActiveReporterMachineId").AsInt64().Nullable()
                .ForeignKey("FK_KubernetesClusters_Machines_ActiveReporter", TableNames.Machines, "Id");

        IfDatabase("SQLite").Execute.Sql("""
            ALTER TABLE "KubernetesClusters"
            ADD COLUMN "ActiveReporterMachineId" INTEGER NULL REFERENCES "Machines" ("Id")
            """);

        // A cluster snapshot identifies its cluster by the UID of the kube-system namespace, so a
        // tenant can hold a UID once. Clusters that only local reports have described have no UID yet
        // and any number of them may exist, so the uniqueness covers reported UIDs only.
        // FluentMigrator's fluent index builder has no partial-index support, so both dialects are
        // raw SQL.
        IfDatabase("PostgreSQL").Execute.Sql("""
            CREATE UNIQUE INDEX "IX_KubernetesClusters_TenantId_KubeSystemUid"
            ON "KubernetesClusters" ("TenantId", "KubeSystemUid") WHERE "KubeSystemUid" IS NOT NULL
            """);

        IfDatabase("SQLite").Execute.Sql("""
            CREATE UNIQUE INDEX "IX_KubernetesClusters_TenantId_KubeSystemUid"
            ON "KubernetesClusters" ("TenantId", "KubeSystemUid") WHERE "KubeSystemUid" IS NOT NULL
            """);

        Alter.Table(TableNames.KubernetesNodes)
            .AddColumn("NodeMachineId").AsString(64).Nullable()
            .AddColumn("Roles").AsString().Nullable()
            .AddColumn("ReadyStatus").AsString(16).Nullable()
            .AddColumn("ReadySince").AsDateTimeOffset().Nullable()
            .AddColumn("Conditions").AsString().Nullable()
            .AddColumn("Unschedulable").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("UnschedulableSince").AsDateTimeOffset().Nullable()
            .AddColumn("ContainerRuntimeVersion").AsString(64).Nullable()
            .AddColumn("PodCount").AsInt32().Nullable()
            .AddColumn("PodCapacity").AsInt32().Nullable()
            .AddColumn("LastReporterSeenAt").AsDateTimeOffset().Nullable();

        // Candidates follow their cluster: removing a cluster removes its candidates. A candidate's
        // machine link is a plain reference, and the unique index on it makes a machine a candidate
        // of one cluster only.
        Create.Table(TableNames.KubernetesReporterCandidates)
            .WithColumn("TenantId").AsInt32().NotNullable()
                .ForeignKey("FK_KubernetesReporterCandidates_Tenants", TableNames.Tenants, "Id")
            .WithColumn("ClusterId").AsInt64().NotNullable()
                .PrimaryKey("PK_KubernetesReporterCandidates")
                .ForeignKey("FK_KubernetesReporterCandidates_KubernetesClusters", TableNames.KubernetesClusters, "Id")
                    .OnDelete(Rule.Cascade)
            .WithColumn("MachineId").AsInt64().NotNullable()
                .PrimaryKey("PK_KubernetesReporterCandidates")
                .ForeignKey("FK_KubernetesReporterCandidates_Machines", TableNames.Machines, "Id")
            .WithColumn("ReporterError").AsString(32).Nullable()
            .WithColumn("LastReportAt").AsDateTimeOffset().NotNullable();

        Create.Index("IX_KubernetesReporterCandidates_MachineId")
            .OnTable(TableNames.KubernetesReporterCandidates)
            .OnColumn("MachineId").Ascending()
            .WithOptions().Unique();

        Create.Index("IX_KubernetesReporterCandidates_TenantId")
            .OnTable(TableNames.KubernetesReporterCandidates)
            .OnColumn("TenantId").Ascending();
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Table(TableNames.KubernetesReporterCandidates);

        Execute.Sql("""DROP INDEX IF EXISTS "IX_KubernetesClusters_TenantId_KubeSystemUid" """);

        Delete.Column("LastReporterSeenAt").FromTable(TableNames.KubernetesNodes);
        Delete.Column("PodCapacity").FromTable(TableNames.KubernetesNodes);
        Delete.Column("PodCount").FromTable(TableNames.KubernetesNodes);
        Delete.Column("ContainerRuntimeVersion").FromTable(TableNames.KubernetesNodes);
        Delete.Column("UnschedulableSince").FromTable(TableNames.KubernetesNodes);
        Delete.Column("Unschedulable").FromTable(TableNames.KubernetesNodes);
        Delete.Column("Conditions").FromTable(TableNames.KubernetesNodes);
        Delete.Column("ReadySince").FromTable(TableNames.KubernetesNodes);
        Delete.Column("ReadyStatus").FromTable(TableNames.KubernetesNodes);
        Delete.Column("Roles").FromTable(TableNames.KubernetesNodes);
        Delete.Column("NodeMachineId").FromTable(TableNames.KubernetesNodes);

        Delete.Column("ActiveReporterMachineId").FromTable(TableNames.KubernetesClusters);
        Delete.Column("LeaseCreatedAt").FromTable(TableNames.KubernetesClusters);
        Delete.Column("LeaseUid").FromTable(TableNames.KubernetesClusters);
        Delete.Column("LastSnapshotTelemetryId").FromTable(TableNames.KubernetesClusters);
        Delete.Column("LastSnapshotReceivedAt").FromTable(TableNames.KubernetesClusters);
        Delete.Column("LeaseTransitions").FromTable(TableNames.KubernetesClusters);
        Delete.Column("Truncated").FromTable(TableNames.KubernetesClusters);
        Delete.Column("RoleOutdated").FromTable(TableNames.KubernetesClusters);
        Delete.Column("ReporterError").FromTable(TableNames.KubernetesClusters);
        Delete.Column("ClusterVersion").FromTable(TableNames.KubernetesClusters);
        Delete.Column("KubeSystemUid").FromTable(TableNames.KubernetesClusters);
    }
}
