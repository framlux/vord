// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;
using System.Data;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Single migration covering the Hangfire-refactor schema changes:
/// <list type="bullet">
///   <item>Creates the dedicated "hangfire" Postgres schema. Hangfire.PostgreSql installs its
///         own tables into this schema on first connection (PrepareSchemaIfNecessary=true).</item>
///   <item>Creates AlertConditionStates — replaces the previous Redis condition keys used by
///         AlertEvaluationJob to enforce DurationMinutes windows. Cascading FKs ensure rows are
///         removed when the parent AlertRule or Machine is deleted.</item>
///   <item>Creates IntegrationDeliveryAttempts — per-(eventId, integrationId) idempotency rows
///         used by IntegrationDeliveryJob to skip already-delivered integrations on Hangfire
///         retry. Cascading FKs to AlertEvents and IntegrationEndpoints.</item>
///   <item>Adds DataExportJobs.StartedAt — used by DataExportProcessingJob's orphan reaper to
///         detect rows stuck in Processing after a worker crash.</item>
/// </list>
/// All work is grouped here because it ships as one deploy unit with the Hangfire migration;
/// none of these changes are independently useful before the rest land.
/// </summary>
[MigrationVersion(2026, 05, 18, 1)]
public sealed class HangfireSchemaMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        IfDatabase("PostgreSQL").Execute.Sql(@"CREATE SCHEMA IF NOT EXISTS ""hangfire"";");

        Create.Table(TableNames.AlertConditionStates)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("AlertRuleId").AsInt32().NotNullable()
                .ForeignKey("FK_AlertConditionStates_AlertRules", TableNames.AlertRules, "Id")
                    .OnDelete(Rule.Cascade)
            .WithColumn("MachineId").AsInt64().NotNullable()
                .ForeignKey("FK_AlertConditionStates_Machines", TableNames.Machines, "Id")
                    .OnDelete(Rule.Cascade)
            .WithColumn("FirstTriggeredAt").AsDateTimeOffset().NotNullable()
            .WithColumn("LastObservedAt").AsDateTimeOffset().NotNullable();

        Create.Index("UX_AlertConditionStates_RuleMachine")
            .OnTable(TableNames.AlertConditionStates)
            .OnColumn("AlertRuleId").Ascending()
            .OnColumn("MachineId").Ascending()
            .WithOptions().Unique();

        // IntegrationDeliveryAttempts deliberately does NOT carry a foreign key to AlertEvents.
        // AlertEvents is range-partitioned by TriggeredAt on Postgres (see InitialMigration line
        // ~437) with a composite primary key (Id, TriggeredAt) — Postgres will not accept an FK
        // referencing only the Id column. Same constraint applies to MachineId on AlertEvents,
        // which is also intentionally not a FK. Application code enforces the relationship; row-
        // level cascade is not useful here because AlertEvents partitions are dropped wholesale
        // via PartitionManagementJob (which does not trigger row-level cascades anyway).
        Create.Table(TableNames.IntegrationDeliveryAttempts)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("AlertEventId").AsInt64().NotNullable()
            .WithColumn("IntegrationEndpointId").AsInt32().NotNullable()
                .ForeignKey("FK_IntegrationDeliveryAttempts_Integrations", TableNames.IntegrationEndpoints, "Id")
                    .OnDelete(Rule.Cascade)
            .WithColumn("SucceededAt").AsDateTimeOffset().NotNullable();

        Create.Index("UX_IntegrationDeliveryAttempts_EventIntegration")
            .OnTable(TableNames.IntegrationDeliveryAttempts)
            .OnColumn("AlertEventId").Ascending()
            .OnColumn("IntegrationEndpointId").Ascending()
            .WithOptions().Unique();

        // Supporting index for application-level "find attempts for this event" lookups, since
        // we removed the FK that would have implied this index.
        Create.Index("IX_IntegrationDeliveryAttempts_AlertEventId")
            .OnTable(TableNames.IntegrationDeliveryAttempts)
            .OnColumn("AlertEventId").Ascending();

        Alter.Table(TableNames.DataExportJobs)
            .AddColumn("StartedAt").AsDateTimeOffset().Nullable();
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Column("StartedAt").FromTable(TableNames.DataExportJobs);
        Delete.Table(TableNames.IntegrationDeliveryAttempts);
        Delete.Table(TableNames.AlertConditionStates);
        IfDatabase("PostgreSQL").Execute.Sql(@"DROP SCHEMA IF EXISTS ""hangfire"" CASCADE;");
    }
}
