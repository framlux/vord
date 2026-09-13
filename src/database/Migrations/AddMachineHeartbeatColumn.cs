// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Records when the server last heard an agent heartbeat, so liveness can be decided from the
/// control plane as well as the telemetry stream. The health sweep runs as SQL against Postgres and
/// cannot read the Redis ping key, so the heartbeat has to reach the same row the sweep reads.
/// Also lowers the shipped heartbeat interval: at the previous 300s it equalled the online
/// threshold, and the agent's ±15% jitter pushed roughly half of all intervals past it.
/// </summary>
[MigrationVersion(2026, 09, 13, 1)]
public sealed class AddMachineHeartbeatColumn : Migration
{
    /// <summary>
    /// Lowers the heartbeat interval only on rows still carrying the shipped value at the seeded
    /// version. Exposed so a test can replay the exact shipped statement rather than a copy of it.
    /// The key literal is <c>ServerConfigurationSettingKeys.AgentHeartbeatSeconds</c>; migrations
    /// pin enum values as literals because a future enum edit must not retroactively change what a
    /// shipped migration did.
    /// </summary>
    public const string ReseedHeartbeatSql =
        $"""
        UPDATE "{TableNames.ServerConfigurationSettings}"
        SET "Value" = '120'
        WHERE "Key" = 1 AND "Version" = 1 AND "Value" = '300'
        """;

    /// <inheritdoc/>
    public override void Up()
    {
        Alter.Table(TableNames.MachineStateSummary)
            .AddColumn("LastHeartbeatAt").AsDateTimeOffset().Nullable();

        // Written by the same sweep that writes HealthStatus, so the staleness rule has exactly one
        // definition. The alert evaluator reads a column rather than recomputing the rule, for the
        // same reason the read paths stopped recomputing health.
        Alter.Table(TableNames.MachineStateSummary)
            .AddColumn("TelemetryStale").AsBoolean().NotNullable().WithDefaultValue(false);

        // Only rows still at the seeded version are moved. UpsertSettingAsync increments Version on
        // every admin write, so Version > 1 means an operator chose this value deliberately.
        Execute.Sql(ReseedHeartbeatSql);
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Column("TelemetryStale").FromTable(TableNames.MachineStateSummary);
        Delete.Column("LastHeartbeatAt").FromTable(TableNames.MachineStateSummary);
    }
}
