// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;
using LinqToDB;
using System.Data;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Initial database migration that creates the full set of core tables for machine management,
/// user accounts, telemetry, alerts, audit logging, integrations, and remote commands, together
/// with all seed data. Time-series tables are range-partitioned by day on PostgreSQL for efficient
/// data retention and archival. The dedicated "hangfire" schema is created here; Hangfire installs
/// its own tables into that schema on first connection.
/// </summary>
[MigrationVersion(2026, 04, 05, 1)]
public sealed class InitialMigration : Migration
{
    /// <summary>
    /// Applies the migration by creating initial database tables, indexes, and seed data.
    /// </summary>
    public override void Up()
    {
        IfDatabase("PostgreSQL").Execute.Sql(@"CREATE SCHEMA IF NOT EXISTS ""hangfire"";");

        Create.Table(TableNames.ServerConfigurationSettings)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity().NotNullable()
            .WithColumn("Key").AsInt32().NotNullable().Indexed()
            .WithColumn("Value").AsString().NotNullable()
            .WithColumn("Version").AsInt32().NotNullable();

        // Seed default configuration values so the admin panel displays settings before manual edits.
        Insert.IntoTable(TableNames.ServerConfigurationSettings)
            .Row(new { Key = 1, Value = "300", Version = 1 })
            .Row(new { Key = 2, Value = "900", Version = 1 })
            .Row(new { Key = 3, Value = "300", Version = 1 })
            .Row(new { Key = 4, Value = "30", Version = 1 })
            .Row(new { Key = 5, Value = "7", Version = 1 })
            .Row(new { Key = 6, Value = "300", Version = 1 })
            .Row(new { Key = 7, Value = "30", Version = 1 })
            .Row(new { Key = 8, Value = "true", Version = 1 })
            .Row(new { Key = 9, Value = "60", Version = 1 })
            .Row(new { Key = 10, Value = "900", Version = 1 })
            .Row(new { Key = 11, Value = "15", Version = 1 })
            .Row(new { Key = 12, Value = "300", Version = 1 })
            .Row(new { Key = 14, Value = "3600", Version = 1 });

        Create.Table(TableNames.Users)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity().NotNullable()
            .WithColumn("ExternalId").AsString().NotNullable().Unique()
            .WithColumn("Username").AsString().NotNullable()
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("CreatedByUserId").AsInt32().NotNullable()
            .WithColumn("IsActive").AsBoolean().NotNullable()
            .WithColumn("IsSystem").AsBoolean().NotNullable()
            .WithColumn("IsGlobalAdmin").AsBoolean().NotNullable()
            .WithColumn("AuthProvider").AsInt16().NotNullable().WithDefaultValue(0)
            .WithColumn("DeletedOn").AsDateTimeOffset().Nullable()
            .WithColumn("DeletedByUserId").AsInt32().Nullable();

        // This must be done here before we add foreign key constraints otherwise the insert
        // will fail due to the CreatedByUserId constraint.
        Insert.IntoTable(TableNames.Users)
            .Row(new
            {
                ExternalId = "system",
                Username = "system",
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = 1,
                IsActive = true,
                IsSystem = true,
                IsGlobalAdmin = true,
            });

        Create.Table(TableNames.Tenants)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity().NotNullable()
            .WithColumn("ExternalId").AsString().NotNullable().Unique()
            .WithColumn("Name").AsString().NotNullable()
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("CreatedByUserId").AsInt32().NotNullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("IsActive").AsBoolean().NotNullable()
            .WithColumn("DisabledAt").AsDateTimeOffset().Nullable()
            .WithColumn("DisabledByUserId").AsInt32().Nullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("LogoUrl").AsString().NotNullable();

        // Unique constraint on tenant name (case-insensitive)
        IfDatabase("PostgreSQL").Execute.Sql(
            "CREATE UNIQUE INDEX \"IX_Tenants_Name\" ON \"Tenants\" (LOWER(\"Name\"))");
        IfDatabase("SQLite").Execute.Sql(
            "CREATE UNIQUE INDEX \"IX_Tenants_Name\" ON \"Tenants\" (\"Name\" COLLATE NOCASE)");

        Create.Table(TableNames.UserTenantRoles)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity().NotNullable()
            .WithColumn("UserId").AsInt32().NotNullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("AssignedTenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id")
            .WithColumn("Role").AsInt32().NotNullable()
            .WithColumn("AssignedByUserId").AsInt32().NotNullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("AssignedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("IsActive").AsBoolean().NotNullable()
            .WithColumn("DisabledByUserId").AsInt32().Nullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("DisabledAt").AsDateTimeOffset().Nullable();

        // Ensure only one active role per user-tenant pair while preserving role change history.
        IfDatabase("PostgreSQL").Execute.Sql(
            @"CREATE UNIQUE INDEX ""IX_UserTenantRoles_Active""
              ON ""UserTenantRoles"" (""UserId"", ""AssignedTenantId"")
              WHERE ""IsActive"" = true");
        IfDatabase("SQLite").Execute.Sql(
            @"CREATE UNIQUE INDEX ""IX_UserTenantRoles_Active""
              ON ""UserTenantRoles"" (""UserId"", ""AssignedTenantId"")
              WHERE ""IsActive"" = 1");

        Create.Table(TableNames.Machines)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id").Indexed()
            .WithColumn("ApiKeyHash").AsString(64).NotNullable().Unique()
            .WithColumn("KeyDeliveredAt").AsDateTimeOffset().Nullable()
            .WithColumn("Name").AsString(250).NotNullable()
            .WithColumn("Description").AsString().Nullable()
            .WithColumn("Location").AsString(250).Nullable()
            .WithColumn("SerialNumber").AsString(64).NotNullable()
            .WithColumn("SystemId").AsString(64).NotNullable()
            .WithColumn("AssetTagNumber").AsString(64).Nullable()
            .WithColumn("MachineType").AsByte().NotNullable()
            .WithColumn("OperatingSystem").AsByte().NotNullable()
            .WithColumn("RegistrationTokenId").AsInt64().NotNullable()
            .WithColumn("RegisteredOn").AsDateTimeOffset().NotNullable().Indexed()
            .WithColumn("IsDeleted").AsBoolean().NotNullable()
            .WithColumn("DeletedOn").AsDateTimeOffset().Nullable()
            .WithColumn("DeletedByUserId").AsInt32().Nullable();

        Create.Index("IX_Machines_SerialNumber_SystemId")
            .OnTable(TableNames.Machines)
            .OnColumn("SerialNumber")
            .Ascending()
            .OnColumn("SystemId")
            .Ascending();

        // Limits live in TierFeatureLimits/TenantSubscriptionOverrides; billing pending-state lives
        // in the billing-api. CancelAtPeriodEnd is mirrored locally for the renewal-prompt flow.
        Create.Table(TableNames.TenantSubscriptions)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id").Unique()
            .WithColumn("Tier").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("CurrentPeriodEnd").AsDateTimeOffset().Nullable()
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("UpdatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("CancelAtPeriodEnd").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.Table(TableNames.TenantOidcConfigurations)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id").Unique()
            .WithColumn("Authority").AsString(500).NotNullable()
            .WithColumn("ClientId").AsString(500).NotNullable()
            .WithColumn("ClientSecret").AsString(2000).NotNullable()
            .WithColumn("MetadataAddress").AsString(500).Nullable()
            .WithColumn("EmailDomain").AsString(255).NotNullable()
            .WithColumn("IsEnabled").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("UpdatedAt").AsDateTimeOffset().NotNullable();

        // MachineTelemetry: range-partitioned by ReceivedAt on Postgres, standard table on SQLite.
        IfDatabase("SQLite").Create.Table(TableNames.MachineTelemetry)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
            .WithColumn("MachineId").AsInt64().NotNullable().ForeignKey(TableNames.Machines, "Id")
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id")
            .WithColumn("TelemetryType").AsInt16().NotNullable()
            .WithColumn("Payload").AsString().NotNullable()
            .WithColumn("ReceivedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("SourceEventId").AsString(64).Nullable();

        IfDatabase("PostgreSQL").Execute.Sql("""
            CREATE TABLE "MachineTelemetry" (
                "Id" BIGINT GENERATED BY DEFAULT AS IDENTITY NOT NULL,
                "MachineId" BIGINT NOT NULL REFERENCES "Machines" ("Id"),
                "TenantId" INTEGER NOT NULL REFERENCES "Tenants" ("Id"),
                "TelemetryType" SMALLINT NOT NULL,
                "Payload" TEXT NOT NULL,
                "ReceivedAt" TIMESTAMPTZ NOT NULL,
                "SourceEventId" VARCHAR(64),
                PRIMARY KEY ("Id", "ReceivedAt")
            ) PARTITION BY RANGE ("ReceivedAt")
        """);

        // Single-column indexes on MachineTelemetry.
        Create.Index("IX_MachineTelemetry_MachineId")
            .OnTable(TableNames.MachineTelemetry)
            .OnColumn("MachineId").Ascending();

        Create.Index("IX_MachineTelemetry_TelemetryType")
            .OnTable(TableNames.MachineTelemetry)
            .OnColumn("TelemetryType").Ascending();

        Create.Index("IX_MachineTelemetry_ReceivedAt")
            .OnTable(TableNames.MachineTelemetry)
            .OnColumn("ReceivedAt").Ascending();

        // Composite index for efficient historical queries.
        Create.Index("IX_MachineTelemetry_Machine_Type_Time")
            .OnTable(TableNames.MachineTelemetry)
            .OnColumn("MachineId").Ascending()
            .OnColumn("TelemetryType").Ascending()
            .OnColumn("ReceivedAt").Descending();

        // Index on MachineTelemetry for tenant-scoped retention queries.
        Create.Index("IX_MachineTelemetry_TenantId_ReceivedAt")
            .OnTable(TableNames.MachineTelemetry)
            .OnColumn("TenantId").Ascending()
            .OnColumn("ReceivedAt").Descending();

        // Unique partial index on SourceEventId for dedup safety net.
        // On Postgres the partition key must be included in unique indexes.
        IfDatabase("PostgreSQL").Execute.Sql(
            @"CREATE UNIQUE INDEX ""IX_MachineTelemetry_SourceEventId""
              ON ""MachineTelemetry"" (""SourceEventId"", ""ReceivedAt"")
              WHERE ""SourceEventId"" IS NOT NULL");
        IfDatabase("SQLite").Execute.Sql(
            @"CREATE UNIQUE INDEX ""IX_MachineTelemetry_SourceEventId""
              ON ""MachineTelemetry"" (""SourceEventId"")
              WHERE ""SourceEventId"" IS NOT NULL");

        Create.Table(TableNames.TenantInvitations)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id")
            .WithColumn("Email").AsString(320).NotNullable()
            .WithColumn("TokenHash").AsString(64).NotNullable().Unique()
            .WithColumn("Role").AsInt32().NotNullable()
            .WithColumn("Status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("InvitedByUserId").AsInt32().NotNullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("AcceptedByUserId").AsInt32().Nullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("ExpiresAt").AsDateTimeOffset().NotNullable()
            .WithColumn("AcceptedAt").AsDateTimeOffset().Nullable()
            .WithColumn("RevokedAt").AsDateTimeOffset().Nullable();

        Create.Index("IX_TenantInvitations_Email_TenantId")
            .OnTable(TableNames.TenantInvitations)
            .OnColumn("Email").Ascending()
            .OnColumn("TenantId").Ascending();

        // Slim summary table for fleet-level queries (search, overview, dashboard, alerts).
        // Row INSERT'd at machine registration; pure UPDATEs thereafter by the streaming worker.
        Create.Table(TableNames.MachineStateSummary)
            .WithColumn("MachineId").AsInt64().PrimaryKey().NotNullable().ForeignKey(TableNames.Machines, "Id")
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id")
            .WithColumn("Name").AsString(250).NotNullable()
            .WithColumn("OperatingSystem").AsByte().NotNullable()
            .WithColumn("MachineType").AsByte().NotNullable()
            .WithColumn("Hostname").AsString(255).Nullable()
            .WithColumn("HardwareModel").AsString(255).Nullable()
            .WithColumn("IpAddresses").AsString().Nullable()
            .WithColumn("OsName").AsString(255).Nullable()
            .WithColumn("OsVersion").AsString(64).Nullable()
            .WithColumn("CpuUsagePercent").AsInt32().Nullable()
            .WithColumn("MemoryUsagePercent").AsInt32().Nullable()
            .WithColumn("MaxDiskUsagePercent").AsInt32().Nullable()
            .WithColumn("PendingUpdates").AsInt32().Nullable()
            .WithColumn("SecurityUpdates").AsInt32().Nullable()
            .WithColumn("FailedServices").AsInt32().Nullable()
            .WithColumn("TotalServices").AsInt32().Nullable()
            .WithColumn("HasDiskHealthIssue").AsBoolean().Nullable()
            .WithColumn("HasHardwareIssue").AsBoolean().Nullable()
            .WithColumn("HealthStatus").AsInt16().NotNullable().WithDefaultValue(0)
            .WithColumn("LastSeenAt").AsDateTimeOffset().Nullable();

        IfDatabase("PostgreSQL").Execute.Sql("""
            ALTER TABLE "MachineStateSummary" ALTER COLUMN "IpAddresses" TYPE jsonb USING "IpAddresses"::jsonb;
        """);

        Create.Index("IX_Summary_TenantId_HealthStatus")
            .OnTable(TableNames.MachineStateSummary)
            .OnColumn("TenantId").Ascending()
            .OnColumn("HealthStatus").Ascending();

        Create.Index("IX_Summary_LastSeenAt")
            .OnTable(TableNames.MachineStateSummary)
            .OnColumn("LastSeenAt").Descending();

        Create.Index("IX_Summary_CpuUsagePercent")
            .OnTable(TableNames.MachineStateSummary)
            .OnColumn("CpuUsagePercent").Ascending();

        Create.Index("IX_Summary_MemoryUsagePercent")
            .OnTable(TableNames.MachineStateSummary)
            .OnColumn("MemoryUsagePercent").Ascending();

        Create.Index("IX_Summary_MaxDiskUsagePercent")
            .OnTable(TableNames.MachineStateSummary)
            .OnColumn("MaxDiskUsagePercent").Ascending();

        Create.Index("IX_Summary_PendingUpdates")
            .OnTable(TableNames.MachineStateSummary)
            .OnColumn("PendingUpdates").Ascending();

        Create.Index("IX_Summary_SecurityUpdates")
            .OnTable(TableNames.MachineStateSummary)
            .OnColumn("SecurityUpdates").Ascending();

        Create.Index("IX_Summary_FailedServices")
            .OnTable(TableNames.MachineStateSummary)
            .OnColumn("FailedServices").Ascending();

        // Cold detail table for single-machine views. No secondary indexes.
        Create.Table(TableNames.MachineStateDetail)
            .WithColumn("MachineId").AsInt64().PrimaryKey().NotNullable().ForeignKey(TableNames.Machines, "Id")
            .WithColumn("HardwareVendor").AsString(255).Nullable()
            .WithColumn("HardwareSerial").AsString(255).Nullable()
            .WithColumn("CpuBrand").AsString(255).Nullable()
            .WithColumn("CpuCores").AsInt32().Nullable()
            .WithColumn("MemoryTotalBytes").AsInt64().Nullable()
            .WithColumn("UptimeSeconds").AsInt64().Nullable()
            .WithColumn("BiosVersion").AsString(64).Nullable()
            .WithColumn("Kernel").AsString(255).Nullable()
            .WithColumn("CpuType").AsString(64).Nullable()
            .WithColumn("CpuPhysicalCpus").AsInt32().Nullable()
            .WithColumn("CpuLogicalCpus").AsInt32().Nullable()
            .WithColumn("SwapTotalBytes").AsInt64().Nullable()
            .WithColumn("SwapFreeBytes").AsInt64().Nullable()
            .WithColumn("MemoryUsedBytes").AsInt64().Nullable()
            .WithColumn("DiskInfos").AsString().Nullable()
            .WithColumn("DiskUsages").AsString().Nullable()
            .WithColumn("SshSessions").AsString().Nullable()
            .WithColumn("HardwareHealth").AsString().Nullable();

        IfDatabase("PostgreSQL").Execute.Sql("""
            ALTER TABLE "MachineStateDetail" ALTER COLUMN "DiskInfos" TYPE jsonb USING "DiskInfos"::jsonb;
            ALTER TABLE "MachineStateDetail" ALTER COLUMN "DiskUsages" TYPE jsonb USING "DiskUsages"::jsonb;
            ALTER TABLE "MachineStateDetail" ALTER COLUMN "SshSessions" TYPE jsonb USING "SshSessions"::jsonb;
            ALTER TABLE "MachineStateDetail" ALTER COLUMN "HardwareHealth" TYPE jsonb USING "HardwareHealth"::jsonb;
        """);

        Create.Table(TableNames.RegistrationTokens)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("TenantId").AsInt32().NotNullable()
                .ForeignKey("FK_RegistrationTokens_Tenants", TableNames.Tenants, "Id")
            .WithColumn("TokenHash").AsString(64).NotNullable().Unique()
            .WithColumn("Name").AsString(250).NotNullable()
            .WithColumn("CreatedByUserId").AsInt32().NotNullable()
                .ForeignKey("FK_RegistrationTokens_Users", TableNames.Users, "Id")
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("IsRevoked").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("RevokedAt").AsDateTimeOffset().Nullable()
            // Bounded token lifetime with a far-future default so a token without an explicit
            // expiry never silently expires.
            .WithColumn("ExpiresAt").AsDateTimeOffset().NotNullable()
                .WithDefaultValue(new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero));

        // Partial index on Machines(TenantId) for active machines only.
        IfDatabase("PostgreSQL").Execute.Sql(
            @"CREATE INDEX ""IX_Machines_TenantId_Active""
              ON ""Machines"" (""TenantId"")
              WHERE ""IsDeleted"" = false");
        IfDatabase("SQLite").Execute.Sql(
            @"CREATE INDEX ""IX_Machines_TenantId_Active""
              ON ""Machines"" (""TenantId"")
              WHERE ""IsDeleted"" = 0");

        // AuditLog: range-partitioned by Timestamp on Postgres, standard table on SQLite.
        IfDatabase("SQLite").Create.Table(TableNames.AuditLog)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
            .WithColumn("TenantId").AsInt32().Nullable().ForeignKey(TableNames.Tenants, "Id")
            .WithColumn("UserId").AsInt32().Nullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("MachineId").AsInt64().Nullable()
            .WithColumn("Action").AsInt16().NotNullable()
            .WithColumn("ResourceType").AsInt16().NotNullable()
            .WithColumn("ResourceId").AsString(255).Nullable()
            .WithColumn("Details").AsString().Nullable()
            .WithColumn("IpAddress").AsString(45).Nullable()
            .WithColumn("Timestamp").AsDateTimeOffset().NotNullable();

        IfDatabase("PostgreSQL").Execute.Sql("""
            CREATE TABLE "AuditLog" (
                "Id" BIGINT GENERATED BY DEFAULT AS IDENTITY NOT NULL,
                "TenantId" INTEGER REFERENCES "Tenants" ("Id"),
                "UserId" INTEGER REFERENCES "UserAccounts" ("Id"),
                "MachineId" BIGINT,
                "Action" SMALLINT NOT NULL,
                "ResourceType" SMALLINT NOT NULL,
                "ResourceId" VARCHAR(255),
                "Details" JSONB,
                "IpAddress" VARCHAR(45),
                "Timestamp" TIMESTAMPTZ NOT NULL,
                PRIMARY KEY ("Id", "Timestamp")
            ) PARTITION BY RANGE ("Timestamp")
        """);

        Create.Index("IX_AuditLog_TenantId_Timestamp")
            .OnTable(TableNames.AuditLog)
            .OnColumn("TenantId").Ascending()
            .OnColumn("Timestamp").Descending();

        Create.Table(TableNames.AlertRules)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity().NotNullable()
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id").Indexed()
            .WithColumn("Name").AsString(250).NotNullable()
            .WithColumn("Description").AsString().Nullable()
            .WithColumn("Metric").AsInt16().NotNullable()
            .WithColumn("Operator").AsInt16().NotNullable()
            .WithColumn("Threshold").AsDecimal().NotNullable()
            .WithColumn("DurationMinutes").AsInt32().NotNullable()
            .WithColumn("Severity").AsInt16().NotNullable()
            .WithColumn("IsEnabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("NotifyEmail").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("NotifyWebhook").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("IsCustom").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("CreatedByUserId").AsInt32().NotNullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("UpdatedAt").AsDateTimeOffset().NotNullable();

        // CHECK constraint for DurationMinutes: SQLite does not support ALTER TABLE ADD CHECK,
        // so the constraint is only enforced at the database level on PostgreSQL. API-level
        // validation ensures non-negative values on all paths.
        IfDatabase("PostgreSQL").Execute.Sql("""ALTER TABLE "AlertRules" ADD CONSTRAINT "CK_AlertRules_DurationMinutes" CHECK ("DurationMinutes" >= 0)""");

        // AlertEvents: range-partitioned by TriggeredAt on Postgres, standard table on SQLite.
        // The FK to AlertRules is intentionally omitted on both databases: Postgres cannot
        // propagate FK constraints from a range-partitioned parent to child partitions, and the
        // SQLite path is kept aligned with production so the rule-delete flow behaves identically.
        // The FK to Tenants is retained.
        IfDatabase("SQLite").Create.Table(TableNames.AlertEvents)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
            .WithColumn("AlertRuleId").AsInt32().NotNullable()
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id")
            .WithColumn("MachineId").AsInt64().NotNullable()
            .WithColumn("Severity").AsInt16().NotNullable()
            .WithColumn("Message").AsString().NotNullable()
            .WithColumn("Details").AsString().Nullable()
            .WithColumn("Status").AsInt16().NotNullable()
            .WithColumn("TriggeredAt").AsDateTimeOffset().NotNullable()
            .WithColumn("AcknowledgedAt").AsDateTimeOffset().Nullable()
            .WithColumn("AcknowledgedByUserId").AsInt32().Nullable()
            .WithColumn("ResolvedAt").AsDateTimeOffset().Nullable();

        IfDatabase("PostgreSQL").Execute.Sql("""
            CREATE TABLE "AlertEvents" (
                "Id" BIGINT GENERATED BY DEFAULT AS IDENTITY NOT NULL,
                "AlertRuleId" INTEGER NOT NULL,
                "TenantId" INTEGER NOT NULL REFERENCES "Tenants" ("Id"),
                "MachineId" BIGINT NOT NULL,
                "Severity" SMALLINT NOT NULL,
                "Message" TEXT NOT NULL,
                "Details" JSONB,
                "Status" SMALLINT NOT NULL,
                "TriggeredAt" TIMESTAMPTZ NOT NULL,
                "AcknowledgedAt" TIMESTAMPTZ,
                "AcknowledgedByUserId" INTEGER,
                "ResolvedAt" TIMESTAMPTZ,
                PRIMARY KEY ("Id", "TriggeredAt")
            ) PARTITION BY RANGE ("TriggeredAt")
        """);

        Create.Index("IX_AlertEvents_TenantId_TriggeredAt")
            .OnTable(TableNames.AlertEvents)
            .OnColumn("TenantId").Ascending()
            .OnColumn("TriggeredAt").Descending();

        // Composite index for efficient dedup checks during alert evaluation.
        Create.Index("IX_AlertEvents_RuleId_MachineId_Status")
            .OnTable(TableNames.AlertEvents)
            .OnColumn("AlertRuleId").Ascending()
            .OnColumn("MachineId").Ascending()
            .OnColumn("Status").Ascending();

        // Join table scoping alert rules to specific machines. Composite primary key, cascade on
        // rule delete. Raw SQL keeps the column types aligned with the shipped schema.
        Execute.Sql($"""
            CREATE TABLE "{TableNames.AlertRuleMachines}" (
                "AlertRuleId" INTEGER NOT NULL,
                "MachineId" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                PRIMARY KEY ("AlertRuleId", "MachineId"),
                FOREIGN KEY ("AlertRuleId") REFERENCES "{TableNames.AlertRules}" ("Id") ON DELETE CASCADE,
                FOREIGN KEY ("MachineId") REFERENCES "{TableNames.Machines}" ("Id")
            )
            """);

        Create.Index("IX_AlertRuleMachines_MachineId")
            .OnTable(TableNames.AlertRuleMachines)
            .OnColumn("MachineId").Ascending()
            .OnColumn("AlertRuleId").Ascending();

        // Per-(rule, machine) condition state replacing the previous Redis condition keys used to
        // enforce DurationMinutes windows. Cascading FKs remove rows with the parent rule/machine.
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

        // Pre-built and custom webhook integrations. Replaces the removed WebhookEndpoints table.
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

        // Per-(eventId, integrationId) idempotency rows used to skip already-delivered integrations
        // on retry. The definition diverges by provider only in the AlertEvents relationship: on
        // Postgres, AlertEvents is range-partitioned with a composite primary key, so a single-column
        // foreign key to it is rejected and the application enforces the relationship instead; on
        // SQLite (the in-memory test database) AlertEvents is a plain table, so a cascading foreign
        // key to it is both valid and relied upon by tests. Everything else is identical.
        IfDatabase("PostgreSQL").Create.Table(TableNames.IntegrationDeliveryAttempts)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("AlertEventId").AsInt64().NotNullable()
            .WithColumn("IntegrationEndpointId").AsInt32().NotNullable()
                .ForeignKey("FK_IntegrationDeliveryAttempts_Integrations", TableNames.IntegrationEndpoints, "Id")
                    .OnDelete(Rule.Cascade)
            .WithColumn("SucceededAt").AsDateTimeOffset().Nullable()
            .WithColumn("Status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("AttemptedAt").AsDateTimeOffset().NotNullable();

        IfDatabase("SQLite").Create.Table(TableNames.IntegrationDeliveryAttempts)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("AlertEventId").AsInt64().NotNullable()
                .ForeignKey("FK_IntegrationDeliveryAttempts_AlertEvents", TableNames.AlertEvents, "Id")
                    .OnDelete(Rule.Cascade)
            .WithColumn("IntegrationEndpointId").AsInt32().NotNullable()
                .ForeignKey("FK_IntegrationDeliveryAttempts_Integrations", TableNames.IntegrationEndpoints, "Id")
                    .OnDelete(Rule.Cascade)
            .WithColumn("SucceededAt").AsDateTimeOffset().Nullable()
            .WithColumn("Status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("AttemptedAt").AsDateTimeOffset().NotNullable();

        Create.Index("UX_IntegrationDeliveryAttempts_EventIntegration")
            .OnTable(TableNames.IntegrationDeliveryAttempts)
            .OnColumn("AlertEventId").Ascending()
            .OnColumn("IntegrationEndpointId").Ascending()
            .WithOptions().Unique();

        Create.Index("IX_IntegrationDeliveryAttempts_AlertEventId")
            .OnTable(TableNames.IntegrationDeliveryAttempts)
            .OnColumn("AlertEventId").Ascending();

        Create.Table(TableNames.DataExportJobs)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity().NotNullable()
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id")
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("RequestedByUserId").AsInt32().NotNullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("RequestedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("CompletedAt").AsDateTimeOffset().Nullable()
            .WithColumn("ObjectKey").AsString().NotNullable().WithDefaultValue("")
            .WithColumn("ExpiresAt").AsDateTimeOffset().NotNullable().WithDefaultValue("1970-01-01T00:00:00+00:00")
            .WithColumn("DownloadToken").AsString(64).NotNullable().WithDefaultValue("")
            .WithColumn("ErrorMessage").AsString().Nullable()
            .WithColumn("FileSizeBytes").AsInt64().Nullable()
            // Used by the orphan reaper to detect rows stuck in Processing after a worker crash.
            .WithColumn("StartedAt").AsDateTimeOffset().Nullable()
            // Bounded retry budget so a permanently broken job stops generating failed entries.
            .WithColumn("FailureCount").AsInt32().NotNullable().WithDefaultValue(0);

        Create.Table(TableNames.UserSigningKeys)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity().NotNullable()
            .WithColumn("UserId").AsInt32().NotNullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id")
            .WithColumn("Label").AsString(250).NotNullable()
            .WithColumn("PublicKey").AsString(64).NotNullable()
            .WithColumn("PublicKeyFingerprint").AsString(64).NotNullable().Unique()
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("RevokedAt").AsDateTimeOffset().Nullable()
            .WithColumn("RevokedByUserId").AsInt32().Nullable().ForeignKey(TableNames.Users, "Id");

        Create.Index("IX_UserSigningKeys_UserId_TenantId")
            .OnTable(TableNames.UserSigningKeys)
            .OnColumn("UserId").Ascending()
            .OnColumn("TenantId").Ascending();

        // Per-machine signing key authorizations. Replaces the removed MachineCertificates table.
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

        // RemoteCommands: range-partitioned by CreatedAt on Postgres, standard table on SQLite.
        IfDatabase("SQLite").Create.Table(TableNames.RemoteCommands)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
            .WithColumn("CommandId").AsString(36).NotNullable()
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id")
            .WithColumn("MachineId").AsInt64().NotNullable().ForeignKey(TableNames.Machines, "Id")
            .WithColumn("UserId").AsInt32().NotNullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("SigningKeyId").AsInt32().NotNullable().ForeignKey(TableNames.UserSigningKeys, "Id")
            .WithColumn("CommandType").AsString(50).NotNullable()
            .WithColumn("Params").AsString().Nullable()
            .WithColumn("Nonce").AsString(32).NotNullable()
            .WithColumn("Signature").AsString(128).NotNullable()
            .WithColumn("CanonicalPayload").AsString().NotNullable()
            .WithColumn("Timestamp").AsDateTimeOffset().NotNullable()
            .WithColumn("ExpiresAt").AsDateTimeOffset().NotNullable()
            .WithColumn("Status").AsInt16().NotNullable()
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("DeliveredAt").AsDateTimeOffset().Nullable()
            .WithColumn("CompletedAt").AsDateTimeOffset().Nullable()
            .WithColumn("ExitCode").AsInt32().Nullable()
            .WithColumn("Stdout").AsString().Nullable()
            .WithColumn("Stderr").AsString().Nullable()
            .WithColumn("ResultMessage").AsString().Nullable();

        IfDatabase("PostgreSQL").Execute.Sql("""
            CREATE TABLE "RemoteCommands" (
                "Id" BIGINT GENERATED BY DEFAULT AS IDENTITY NOT NULL,
                "CommandId" VARCHAR(36) NOT NULL,
                "TenantId" INTEGER NOT NULL REFERENCES "Tenants" ("Id"),
                "MachineId" BIGINT NOT NULL REFERENCES "Machines" ("Id"),
                "UserId" INTEGER NOT NULL REFERENCES "UserAccounts" ("Id"),
                "SigningKeyId" INTEGER NOT NULL REFERENCES "UserSigningKeys" ("Id"),
                "CommandType" VARCHAR(50) NOT NULL,
                "Params" JSONB,
                "Nonce" VARCHAR(32) NOT NULL,
                "Signature" VARCHAR(128) NOT NULL,
                "CanonicalPayload" TEXT NOT NULL,
                "Timestamp" TIMESTAMPTZ NOT NULL,
                "ExpiresAt" TIMESTAMPTZ NOT NULL,
                "Status" SMALLINT NOT NULL,
                "CreatedAt" TIMESTAMPTZ NOT NULL,
                "DeliveredAt" TIMESTAMPTZ,
                "CompletedAt" TIMESTAMPTZ,
                "ExitCode" INTEGER,
                "Stdout" TEXT,
                "Stderr" TEXT,
                "ResultMessage" TEXT,
                PRIMARY KEY ("Id", "CreatedAt")
            ) PARTITION BY RANGE ("CreatedAt")
        """);

        Create.Index("IX_RemoteCommands_MachineId_Status")
            .OnTable(TableNames.RemoteCommands)
            .OnColumn("MachineId").Ascending()
            .OnColumn("Status").Ascending();

        Create.Index("IX_RemoteCommands_TenantId_CreatedAt")
            .OnTable(TableNames.RemoteCommands)
            .OnColumn("TenantId").Ascending()
            .OnColumn("CreatedAt").Descending();

        // Unique index on CommandId. On Postgres the partition key must be included.
        IfDatabase("PostgreSQL").Execute.Sql(
            @"CREATE UNIQUE INDEX ""IX_RemoteCommands_CommandId""
              ON ""RemoteCommands"" (""CommandId"", ""CreatedAt"")");
        IfDatabase("SQLite").Execute.Sql(
            @"CREATE UNIQUE INDEX ""IX_RemoteCommands_CommandId""
              ON ""RemoteCommands"" (""CommandId"")");

        // Database-managed per-tier feature limits, replacing the previously hardcoded
        // SubscriptionOptions configuration.
        Create.Table("TierFeatureLimits")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Tier").AsInt32().NotNullable().Unique()
            .WithColumn("MachineLimit").AsInt32().NotNullable()
            .WithColumn("RetentionDays").AsInt32().NotNullable()
            .WithColumn("AlertRuleLimit").AsInt32().NotNullable()
            .WithColumn("WebhookLimit").AsInt32().NotNullable()
            .WithColumn("UpdatedAt").AsDateTimeOffset().NotNullable();

        // Default per-tier feature limits (Free=1, Pro=2, Team=3).
        Insert.IntoTable("TierFeatureLimits").Row(new
        {
            Tier = 1,
            MachineLimit = 3,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        Insert.IntoTable("TierFeatureLimits").Row(new
        {
            Tier = 2,
            MachineLimit = 1000,
            RetentionDays = 60,
            AlertRuleLimit = 10,
            WebhookLimit = 5,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        Insert.IntoTable("TierFeatureLimits").Row(new
        {
            Tier = 3,
            MachineLimit = 10000,
            RetentionDays = 365,
            AlertRuleLimit = 25,
            WebhookLimit = 15,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Per-customer limit overrides layered on top of the tier defaults.
        Create.Table("TenantSubscriptionOverrides")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("TenantId").AsInt32().NotNullable().Unique()
                .ForeignKey("FK_TenantSubscriptionOverrides_Tenants", TableNames.Tenants, "Id")
            .WithColumn("MachineLimit").AsInt32().Nullable()
            .WithColumn("RetentionDays").AsInt32().Nullable()
            .WithColumn("AlertRuleLimit").AsInt32().Nullable()
            .WithColumn("WebhookLimit").AsInt32().Nullable()
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("UpdatedAt").AsDateTimeOffset().NotNullable();

        // Create initial daily partitions and default partitions for all partitioned tables.
        CreateInitialDailyPartitions(TableNames.MachineTelemetry);
        CreateInitialDailyPartitions(TableNames.AuditLog);
        CreateInitialDailyPartitions(TableNames.AlertEvents);
        CreateInitialDailyPartitions(TableNames.RemoteCommands);
    }

    /// <summary>
    /// Reverts the migration by dropping all tables, indexes, and the hangfire schema.
    /// </summary>
    public override void Down()
    {
        IfDatabase("PostgreSQL").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_Machines_TenantId_Active""");
        IfDatabase("SQLite").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_Machines_TenantId_Active""");
        IfDatabase("PostgreSQL").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_MachineTelemetry_SourceEventId""");
        IfDatabase("SQLite").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_MachineTelemetry_SourceEventId""");
        IfDatabase("PostgreSQL").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_RemoteCommands_CommandId""");
        IfDatabase("SQLite").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_RemoteCommands_CommandId""");
        IfDatabase("PostgreSQL").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_IntegrationEndpoints_TenantId""");
        IfDatabase("SQLite").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_IntegrationEndpoints_TenantId""");
        IfDatabase("PostgreSQL").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_IntegrationEndpoints_TenantId_Provider""");
        IfDatabase("SQLite").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_IntegrationEndpoints_TenantId_Provider""");

        Delete.Table("TenantSubscriptionOverrides");
        Delete.Table("TierFeatureLimits");
        Delete.Table(TableNames.RemoteCommands);
        Delete.Table(TableNames.IntegrationDeliveryAttempts);
        Delete.Table(TableNames.IntegrationEndpoints);
        Delete.Table(TableNames.AlertConditionStates);
        Delete.Table(TableNames.AlertRuleMachines);
        Delete.Table(TableNames.MachineAuthorizedKeys);
        Delete.Table(TableNames.UserSigningKeys);
        Delete.Table(TableNames.DataExportJobs);
        Delete.Table(TableNames.AlertEvents);
        Delete.Table(TableNames.AlertRules);
        Delete.Table(TableNames.AuditLog);
        Delete.Table(TableNames.RegistrationTokens);
        Delete.Table(TableNames.MachineStateDetail);
        Delete.Table(TableNames.MachineStateSummary);
        Delete.Table(TableNames.TenantInvitations);
        Delete.Table(TableNames.MachineTelemetry);
        Delete.Table(TableNames.TenantOidcConfigurations);
        Delete.Table(TableNames.TenantSubscriptions);
        Delete.Table(TableNames.Machines);
        Delete.Table(TableNames.UserTenantRoles);
        Delete.Table(TableNames.Tenants);
        Delete.Table(TableNames.Users);
        Delete.Table(TableNames.ServerConfigurationSettings);

        IfDatabase("PostgreSQL").Execute.Sql(@"DROP SCHEMA IF EXISTS ""hangfire"" CASCADE;");
    }

    /// <summary>
    /// Creates a small bootstrap set of daily partitions (today + 7 days ahead) plus a
    /// default partition on PostgreSQL. The PartitionManagementService takes over on
    /// startup and creates future partitions going forward. On SQLite this is a no-op
    /// since partitioning is not supported.
    /// </summary>
    private void CreateInitialDailyPartitions(string tableName)
    {
        string lowerName = tableName.ToLowerInvariant();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        for (int offset = 0; offset <= 7; offset++)
        {
            DateOnly day = today.AddDays(offset);
            DateOnly nextDay = day.AddDays(1);
            string partitionName = $"{lowerName}_d{day:yyyyMMdd}";

            IfDatabase("PostgreSQL").Execute.Sql(
                $@"CREATE TABLE ""{partitionName}"" PARTITION OF ""{tableName}""
                       FOR VALUES FROM ('{day:yyyy-MM-dd}') TO ('{nextDay:yyyy-MM-dd}')");
        }

        IfDatabase("PostgreSQL").Execute.Sql(
            $@"CREATE TABLE ""{lowerName}_default"" PARTITION OF ""{tableName}"" DEFAULT");
    }
}
