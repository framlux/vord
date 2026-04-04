// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;
using LinqToDB;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Initial database migration that creates core tables for machine management and user accounts.
/// </summary>
[MigrationVersion(2025, 09, 13, 1)]
public sealed class InitialMigration : Migration
{
    /// <summary>
    /// Applies the migration by creating initial database tables and indexes.
    /// </summary>
    public override void Up()
    {
        Create.Table(TableNames.ServerConfigurationSettings)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity().NotNullable()
            .WithColumn("Key").AsInt32().NotNullable().Indexed()
            .WithColumn("Value").AsString().NotNullable()
            .WithColumn("Version").AsInt32().NotNullable();

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
        IfDatabase("Postgres").Execute.Sql(
            "CREATE UNIQUE INDEX \"IX_Tenants_Name\" ON \"Tenants\" (LOWER(\"Name\"))");
        IfDatabase("SQLite").Execute.Sql(
            "CREATE UNIQUE INDEX \"IX_Tenants_Name\" ON \"Tenants\" (\"Name\" COLLATE NOCASE)");

        Create.Table(TableNames.UserTenantRoles)
            .WithColumn("UserId").AsInt32().NotNullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("AssignedTenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id")
            .WithColumn("Role").AsInt32().NotNullable()
            .WithColumn("AssignedByUserId").AsInt32().NotNullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("AssignedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("IsActive").AsBoolean().NotNullable()
            .WithColumn("DisabledByUserId").AsInt32().Nullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("DisabledAt").AsDateTimeOffset().Nullable();

        // SQLite does not support ALTER TABLE ADD CONSTRAINT for primary keys.
        // On SQLite, the table works without the explicit composite PK constraint.
        IfDatabase("Postgres")
            .Create.PrimaryKey("PK_UserTenantRoles")
            .OnTable(TableNames.UserTenantRoles)
            .Columns("UserId", "AssignedTenantId");

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

        Create.Table(TableNames.MachineCertificates)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
            .WithColumn("MachineId").AsInt64().NotNullable().ForeignKey(TableNames.Machines, "Id").Indexed()
            .WithColumn("Thumbprint").AsString(128).NotNullable().Unique()
            .WithColumn("IssuedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("ExpiresAt").AsDateTimeOffset().NotNullable()
            .WithColumn("RevokedAt").AsDateTimeOffset().Nullable();

        Create.Table(TableNames.TenantSubscriptions)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id").Unique()
            .WithColumn("Tier").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("StripeCustomerId").AsString(255).Nullable().Indexed()
            .WithColumn("StripeSubscriptionId").AsString(255).Nullable().Indexed()
            .WithColumn("MachineLimit").AsInt32().Nullable()
            .WithColumn("RetentionDays").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("CurrentPeriodEnd").AsDateTimeOffset().Nullable()
            .WithColumn("CancelAtPeriodEnd").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("PendingAction").AsInt16().NotNullable().WithDefaultValue(0)
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("UpdatedAt").AsDateTimeOffset().NotNullable();

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

        Create.Table(TableNames.MachineTelemetry)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
            .WithColumn("MachineId").AsInt64().NotNullable().ForeignKey(TableNames.Machines, "Id").Indexed()
            .WithColumn("TelemetryType").AsInt16().NotNullable().Indexed()
            .WithColumn("Payload").AsString().NotNullable()
            .WithColumn("ReceivedAt").AsDateTimeOffset().NotNullable().Indexed()
            .WithColumn("SourceEventId").AsString(64).Nullable()
            .WithColumn("DeletedAt").AsDateTimeOffset().Nullable();

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

        Create.Table(TableNames.MachineState)
            .WithColumn("MachineId").AsInt64().PrimaryKey().NotNullable().ForeignKey(TableNames.Machines, "Id")

            // SystemInfo (type=1)
            .WithColumn("Hostname").AsString(255).Nullable()
            .WithColumn("HardwareVendor").AsString(255).Nullable()
            .WithColumn("HardwareModel").AsString(255).Nullable()
            .WithColumn("HardwareSerial").AsString(255).Nullable()
            .WithColumn("CpuBrand").AsString(255).Nullable()
            .WithColumn("CpuCores").AsInt32().Nullable()
            .WithColumn("MemoryTotalBytes").AsInt64().Nullable()
            .WithColumn("UptimeSeconds").AsInt64().Nullable()
            .WithColumn("BiosVersion").AsString(64).Nullable()
            .WithColumn("IpAddresses").AsString().Nullable()

            // OsVersion (type=2)
            .WithColumn("OsName").AsString(255).Nullable()
            .WithColumn("OsVersion").AsString(64).Nullable()
            .WithColumn("Kernel").AsString(255).Nullable()

            // CpuInfo (type=3)
            .WithColumn("CpuType").AsString(64).Nullable()
            .WithColumn("CpuPhysicalCpus").AsInt32().Nullable()
            .WithColumn("CpuLogicalCpus").AsInt32().Nullable()
            .WithColumn("CpuInfoAt").AsDateTimeOffset().Nullable()

            // MemoryInfo (type=4)
            .WithColumn("SwapTotalBytes").AsInt64().Nullable()
            .WithColumn("SwapFreeBytes").AsInt64().Nullable()
            .WithColumn("MemoryInfoAt").AsDateTimeOffset().Nullable()

            // DiskInfo (type=5)
            .WithColumn("DiskInfos").AsString().Nullable()
            .WithColumn("DiskInfoAt").AsDateTimeOffset().Nullable()

            // CpuUsage (type=6)
            .WithColumn("CpuUsagePercent").AsInt32().Nullable()

            // MemoryUsage (type=7)
            .WithColumn("MemoryUsedBytes").AsInt64().Nullable()
            .WithColumn("MemoryUsagePercent").AsInt32().Nullable()

            // DiskUsage (type=8)
            .WithColumn("DiskUsages").AsString().Nullable()

            // SSH sessions (type=9)
            .WithColumn("SshSessions").AsString().Nullable()
            .WithColumn("SshSessionsAt").AsDateTimeOffset().Nullable()

            // HardwareHealth (type=10)
            .WithColumn("HardwareHealth").AsString().Nullable()

            // PackageUpdates (type=11)
            .WithColumn("PendingUpdates").AsInt32().Nullable()
            .WithColumn("SecurityUpdates").AsInt32().Nullable()

            // ServiceStatus (type=12)
            .WithColumn("TotalServices").AsInt32().Nullable()
            .WithColumn("FailedServices").AsInt32().Nullable()

            // Computed health
            .WithColumn("HealthStatus").AsInt16().NotNullable().WithDefaultValue(0)

            // Per-type timestamps
            .WithColumn("SystemInfoAt").AsDateTimeOffset().Nullable()
            .WithColumn("OsVersionAt").AsDateTimeOffset().Nullable()
            .WithColumn("CpuUsageAt").AsDateTimeOffset().Nullable()
            .WithColumn("MemoryUsageAt").AsDateTimeOffset().Nullable()
            .WithColumn("DiskUsageAt").AsDateTimeOffset().Nullable()
            .WithColumn("HardwareHealthAt").AsDateTimeOffset().Nullable()
            .WithColumn("PackageUpdatesAt").AsDateTimeOffset().Nullable()
            .WithColumn("ServiceStatusAt").AsDateTimeOffset().Nullable()
            .WithColumn("LastTelemetryAt").AsDateTimeOffset().Nullable()
            .WithColumn("LastPingAt").AsDateTimeOffset().Nullable();

        // Upgrade JSON columns to JSONB on PostgreSQL for indexing and query performance
        IfDatabase("Postgres").Execute.Sql("""
            ALTER TABLE "MachineState" ALTER COLUMN "IpAddresses" TYPE jsonb USING "IpAddresses"::jsonb;
            ALTER TABLE "MachineState" ALTER COLUMN "DiskUsages" TYPE jsonb USING "DiskUsages"::jsonb;
            ALTER TABLE "MachineState" ALTER COLUMN "HardwareHealth" TYPE jsonb USING "HardwareHealth"::jsonb;
        """);

        Create.Table(TableNames.RegistrationTokens)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("TenantId").AsInt32().NotNullable()
                .ForeignKey("FK_RegistrationTokens_Tenants", TableNames.Tenants, "Id")
            .WithColumn("TokenHash").AsString(64).NotNullable().Unique()
            .WithColumn("Name").AsString(250).NotNullable()
            .WithColumn("ExpiresAt").AsDateTimeOffset().NotNullable()
            .WithColumn("MaxUses").AsInt32().NotNullable()
            .WithColumn("UsedCount").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("CreatedByUserId").AsInt32().NotNullable()
                .ForeignKey("FK_RegistrationTokens_Users", TableNames.Users, "Id")
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("IsRevoked").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("RevokedAt").AsDateTimeOffset().Nullable();

        // Composite index for efficient historical queries.
        Create.Index("IX_MachineTelemetry_Machine_Type_Time")
            .OnTable(TableNames.MachineTelemetry)
            .OnColumn("MachineId").Ascending()
            .OnColumn("TelemetryType").Ascending()
            .OnColumn("ReceivedAt").Descending();

        // Upgrade JSON columns to JSONB on PostgreSQL
        IfDatabase("Postgres").Execute.Sql("""
            ALTER TABLE "MachineState" ALTER COLUMN "DiskInfos" TYPE jsonb USING "DiskInfos"::jsonb;
            ALTER TABLE "MachineState" ALTER COLUMN "SshSessions" TYPE jsonb USING "SshSessions"::jsonb;
        """);

        // Unique partial index on SourceEventId for dedup safety net.
        IfDatabase("Postgres").Execute.Sql(
            @"CREATE UNIQUE INDEX ""IX_MachineTelemetry_SourceEventId""
              ON ""MachineTelemetry"" (""SourceEventId"")
              WHERE ""SourceEventId"" IS NOT NULL");
        IfDatabase("SQLite").Execute.Sql(
            @"CREATE UNIQUE INDEX ""IX_MachineTelemetry_SourceEventId""
              ON ""MachineTelemetry"" (""SourceEventId"")
              WHERE ""SourceEventId"" IS NOT NULL");

        // Partial index for active telemetry queries (mirrors composite index but excludes soft-deleted rows).
        IfDatabase("Postgres").Execute.Sql(
            @"CREATE INDEX ""IX_MachineTelemetry_Active""
              ON ""MachineTelemetry"" (""MachineId"", ""TelemetryType"", ""ReceivedAt"" DESC)
              WHERE ""DeletedAt"" IS NULL");
        IfDatabase("SQLite").Execute.Sql(
            @"CREATE INDEX ""IX_MachineTelemetry_Active""
              ON ""MachineTelemetry"" (""MachineId"", ""TelemetryType"", ""ReceivedAt"" DESC)
              WHERE ""DeletedAt"" IS NULL");

        // Index on DeletedAt for efficient cleanup queries.
        IfDatabase("Postgres").Execute.Sql(
            @"CREATE INDEX ""IX_MachineTelemetry_DeletedAt""
              ON ""MachineTelemetry"" (""DeletedAt"")
              WHERE ""DeletedAt"" IS NOT NULL");
        IfDatabase("SQLite").Execute.Sql(
            @"CREATE INDEX ""IX_MachineTelemetry_DeletedAt""
              ON ""MachineTelemetry"" (""DeletedAt"")
              WHERE ""DeletedAt"" IS NOT NULL");

        // Partial index on Machines(TenantId) for active machines only.
        IfDatabase("Postgres").Execute.Sql(
            @"CREATE INDEX ""IX_Machines_TenantId_Active""
              ON ""Machines"" (""TenantId"")
              WHERE ""IsDeleted"" = false");
        IfDatabase("SQLite").Execute.Sql(
            @"CREATE INDEX ""IX_Machines_TenantId_Active""
              ON ""Machines"" (""TenantId"")
              WHERE ""IsDeleted"" = 0");

        // MachineState indexes for SQL-level fleet queries.
        Create.Index("IX_MachineState_HealthStatus")
            .OnTable(TableNames.MachineState)
            .OnColumn("HealthStatus").Ascending();

        Create.Index("IX_MachineState_LastPingAt")
            .OnTable(TableNames.MachineState)
            .OnColumn("LastPingAt").Descending();

        Create.Table(TableNames.AuditLog)
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

        Create.Index("IX_AuditLog_TenantId_Timestamp")
            .OnTable(TableNames.AuditLog)
            .OnColumn("TenantId").Ascending()
            .OnColumn("Timestamp").Descending();

        // Upgrade Details to JSONB on PostgreSQL
        IfDatabase("Postgres").Execute.Sql("""
            ALTER TABLE "AuditLog" ALTER COLUMN "Details" TYPE jsonb USING "Details"::jsonb;
        """);

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

        Create.Table(TableNames.AlertEvents)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
            .WithColumn("AlertRuleId").AsInt32().NotNullable().ForeignKey(TableNames.AlertRules, "Id")
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id")
            .WithColumn("MachineId").AsInt64().NotNullable()
            .WithColumn("Severity").AsInt16().NotNullable()
            .WithColumn("Message").AsString().NotNullable()
            .WithColumn("Details").AsString().Nullable()
            .WithColumn("Status").AsInt16().NotNullable()
            .WithColumn("TriggeredAt").AsDateTimeOffset().NotNullable()
            .WithColumn("AcknowledgedAt").AsDateTimeOffset().Nullable()
            .WithColumn("ResolvedAt").AsDateTimeOffset().Nullable();

        Create.Index("IX_AlertEvents_TenantId_TriggeredAt")
            .OnTable(TableNames.AlertEvents)
            .OnColumn("TenantId").Ascending()
            .OnColumn("TriggeredAt").Descending();

        // Upgrade Details to JSONB on PostgreSQL
        IfDatabase("Postgres").Execute.Sql("""
            ALTER TABLE "AlertEvents" ALTER COLUMN "Details" TYPE jsonb USING "Details"::jsonb;
        """);

        Create.Table(TableNames.WebhookEndpoints)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity().NotNullable()
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id").Indexed()
            .WithColumn("Name").AsString(250).NotNullable()
            .WithColumn("Url").AsString(2000).NotNullable()
            .WithColumn("Secret").AsString(500).NotNullable()
            .WithColumn("IsEnabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("CreatedByUserId").AsInt32().NotNullable().ForeignKey(TableNames.Users, "Id")
            .WithColumn("CreatedAt").AsDateTimeOffset().NotNullable();

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
            .WithColumn("FileSizeBytes").AsInt64().Nullable();

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

        Create.Table(TableNames.RemoteCommands)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
            .WithColumn("CommandId").AsString(36).NotNullable().Unique()
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

        Create.Index("IX_RemoteCommands_MachineId_Status")
            .OnTable(TableNames.RemoteCommands)
            .OnColumn("MachineId").Ascending()
            .OnColumn("Status").Ascending();

        Create.Index("IX_RemoteCommands_TenantId_CreatedAt")
            .OnTable(TableNames.RemoteCommands)
            .OnColumn("TenantId").Ascending()
            .OnColumn("CreatedAt").Descending();

        // Upgrade Params to JSONB on PostgreSQL
        IfDatabase("Postgres").Execute.Sql("""
            ALTER TABLE "RemoteCommands" ALTER COLUMN "Params" TYPE jsonb USING "Params"::jsonb;
        """);
    }

    /// <summary>
    /// Reverts the migration by dropping all initial tables and indexes.
    /// </summary>
    public override void Down()
    {
        IfDatabase("Postgres").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_Machines_TenantId_Active""");
        IfDatabase("SQLite").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_Machines_TenantId_Active""");
        IfDatabase("Postgres").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_MachineTelemetry_DeletedAt""");
        IfDatabase("SQLite").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_MachineTelemetry_DeletedAt""");
        IfDatabase("Postgres").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_MachineTelemetry_Active""");
        IfDatabase("SQLite").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_MachineTelemetry_Active""");
        IfDatabase("Postgres").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_MachineTelemetry_SourceEventId""");
        IfDatabase("SQLite").Execute.Sql(@"DROP INDEX IF EXISTS ""IX_MachineTelemetry_SourceEventId""");

        Delete.Table(TableNames.RemoteCommands);
        Delete.Table(TableNames.UserSigningKeys);
        Delete.Table(TableNames.DataExportJobs);
        Delete.Table(TableNames.WebhookEndpoints);
        Delete.Table(TableNames.AlertEvents);
        Delete.Table(TableNames.AlertRules);
        Delete.Table(TableNames.AuditLog);
        Delete.Table(TableNames.RegistrationTokens);
        Delete.Table(TableNames.MachineState);
        Delete.Table(TableNames.TenantInvitations);
        Delete.Table(TableNames.MachineTelemetry);
        Delete.Table(TableNames.TenantOidcConfigurations);
        Delete.Table(TableNames.TenantSubscriptions);
        Delete.Table(TableNames.MachineCertificates);
        Delete.Table(TableNames.Machines);
        Delete.Table(TableNames.UserTenantRoles);
        Delete.Table(TableNames.Tenants);
        Delete.Table(TableNames.Users);
        Delete.Table(TableNames.ServerConfigurationSettings);
    }
}
